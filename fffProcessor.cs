/*
Copyright (C) 2013 David Braam
Copyright (c) 2014, Lars Brubaker

This file is part of MatterSlice.

MatterSlice is free software: you can redistribute it and/or modify
it under the terms of the GNU Lesser General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

MatterSlice is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with MatterSlice.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;

using MatterSlice.ClipperLib;

namespace MatterHackers.MatterSlice
{
    using Polygon = List<IntPoint>;
    using Polygons = List<List<IntPoint>>;

    // Fused Filament Fabrication processor.
    public class fffProcessor
    {
        int maxObjectHeight;
        int fileNr;
        GCodeExport gcode = new GCodeExport();
        ConfigSettings config;
        Stopwatch timeKeeper = new Stopwatch();

        GCodePathConfig skirtConfig = new GCodePathConfig();
        GCodePathConfig inset0Config = new GCodePathConfig();
        GCodePathConfig insetXConfig = new GCodePathConfig();
        GCodePathConfig fillConfig = new GCodePathConfig();
        GCodePathConfig supportConfig = new GCodePathConfig();

        public fffProcessor(ConfigSettings config)
        {
            this.config = config;
            fileNr = 1;
            maxObjectHeight = 0;
        }

        public bool setTargetFile(string filename)
        {
            gcode.setFilename(filename);
            {
                gcode.writeComment("Generated with MatterSlice {0}".FormatWith(ConfigConstants.VERSION));
            }

            return gcode.isOpened();
        }

        public bool processFile(string input_filename)
        {
            if (!gcode.isOpened())
            {
                return false;
            }

            Stopwatch timeKeeperTotal = new Stopwatch();
            timeKeeperTotal.Start();
            SliceDataStorage storage = new SliceDataStorage();
            preSetup(config.extrusionWidth_um);
            if (!prepareModel(storage, input_filename))
            {
                return false;
            }

            processSliceData(storage);
            writeGCode(storage);

            LogOutput.logProgress("process", 1, 1); //Report to the GUI that a file has been fully processed.
            LogOutput.log("Total time elapsed {0:0.00}s.\n".FormatWith(timeKeeperTotal.Elapsed.Seconds));

            return true;
        }

        public void finalize()
        {
            if (!gcode.isOpened())
            {
                return;
            }

            gcode.finalize(maxObjectHeight, config.travelSpeed, config.endCode);

            gcode.Close();
        }

        void preSetup(int extrusionWidth)
        {
            skirtConfig.setData(config.supportMaterialSpeed, extrusionWidth, "SKIRT");
            inset0Config.setData(config.outsidePerimeterSpeed, extrusionWidth, "WALL-OUTER");
            insetXConfig.setData(config.insidePerimetersSpeed, extrusionWidth, "WALL-INNER");
            fillConfig.setData(config.infillSpeed, extrusionWidth, "FILL");
            supportConfig.setData(config.supportMaterialSpeed, extrusionWidth, "SUPPORT");

            for (int n = 1; n < ConfigConstants.MAX_EXTRUDERS; n++)
            {
                gcode.setExtruderOffset(n, config.extruderOffsets[n]);
            }

            gcode.SetOutputType(config.outputType);
            gcode.setRetractionSettings(config.retractionAmount_um, config.retractionSpeed, config.retractionAmountOnExtruderSwitch_um, config.minimumExtrusionBeforeRetraction_um, config.retractionZHop);
        }

        bool prepareModel(SliceDataStorage storage, string input_filename)
        {
            timeKeeper.Restart();
            LogOutput.log("Loading {0} from disk...\n".FormatWith(input_filename));
            SimpleModel model = SimpleModel.loadModelFromFile(input_filename, config.modelRotationMatrix);
            if (model == null)
            {
                LogOutput.logError("Failed to load model: {0}\n".FormatWith(input_filename));
                return false;
            }
            LogOutput.log("Loaded from disk in {0:0.0}s\n".FormatWith(timeKeeper.Elapsed.Seconds));
            timeKeeper.Restart();
            LogOutput.log("Analyzing and optimizing model...\n");
            OptimizedModel optomizedModel = new OptimizedModel(model, new Point3(config.positionToPlaceObjectCenter_um.X, config.positionToPlaceObjectCenter_um.Y, -config.bottomClipAmount_um), config.centerObjectInXy);
            for (int volumeIndex = 0; volumeIndex < model.volumes.Count; volumeIndex++)
            {
                LogOutput.log("  Face counts: {0} . {1} {2:0.0}%\n".FormatWith((int)model.volumes[volumeIndex].faceTriangles.Count, (int)optomizedModel.volumes[volumeIndex].facesTriangle.Count, (double)(optomizedModel.volumes[volumeIndex].facesTriangle.Count) / (double)(model.volumes[volumeIndex].faceTriangles.Count) * 100));
                LogOutput.log("  Vertex counts: {0} . {1} {2:0.0}%\n".FormatWith((int)model.volumes[volumeIndex].faceTriangles.Count * 3, (int)optomizedModel.volumes[volumeIndex].vertices.Count, (double)(optomizedModel.volumes[volumeIndex].vertices.Count) / (double)(model.volumes[volumeIndex].faceTriangles.Count * 3) * 100));
            }

            LogOutput.log("Optimize model {0:0.0}s \n".FormatWith(timeKeeper.Elapsed.Seconds));
            timeKeeper.Reset();
#if DEBUG
            optomizedModel.saveDebugSTL("debug_output.stl");
#endif

            LogOutput.log("Slicing model...\n");
            List<Slicer> slicerList = new List<Slicer>();
            for (int volumeIdx = 0; volumeIdx < optomizedModel.volumes.Count; volumeIdx++)
            {
                Slicer slicer = new Slicer(optomizedModel.volumes[volumeIdx], config.firstLayerThickness_um, config.layerThickness_um, config.repairOutlines);
                slicerList.Add(slicer);
            }
            LogOutput.log("Sliced model in {0:0.0}s\n".FormatWith(timeKeeper.Elapsed.Seconds));
            timeKeeper.Restart();

            LogOutput.log("Generating support map...\n");
            SupportPolyGenerator.generateSupportGrid(storage.support, optomizedModel, config);

            storage.modelSize = optomizedModel.size;
            storage.modelMin = optomizedModel.minXYZ;
            storage.modelMax = optomizedModel.maxXYZ;

            LogOutput.log("Generating layer parts...\n");
            for (int volumeIdx = 0; volumeIdx < slicerList.Count; volumeIdx++)
            {
                storage.volumes.Add(new SliceVolumeStorage());
                LayerPart.createLayerParts(storage.volumes[volumeIdx], slicerList[volumeIdx], config.repairOverlaps);
                slicerList[volumeIdx] = null;

                if (config.enableRaft)
                {
                    //Add the raft offset to each layer.
                    for (int layerNr = 0; layerNr < storage.volumes[volumeIdx].layers.Count; layerNr++)
                    {
                        storage.volumes[volumeIdx].layers[layerNr].printZ += config.raftBaseThickness_um + config.raftInterfaceThicknes_um;
                    }
                }
            }
            LogOutput.log("Generated layer parts in {0:0.0}s\n".FormatWith(timeKeeper.Elapsed.Seconds));
            timeKeeper.Restart();
            return true;
        }

        void processSliceData(SliceDataStorage storage)
        {
            //carveMultipleVolumes(storage.volumes);
            MultiVolumes.generateMultipleVolumesOverlap(storage.volumes, config.multiVolumeOverlapPercent);
#if DEBUG
            LayerPart.dumpLayerparts(storage, "output.html");
#endif

            int totalLayers = storage.volumes[0].layers.Count;
            for (int layerIndex = 0; layerIndex < totalLayers; layerIndex++)
            {
                for (int volumeIndex = 0; volumeIndex < storage.volumes.Count; volumeIndex++)
                {
                    int insetCount = config.numberOfPerimeters;
                    if (config.continuousSpiralOuterPerimeter && (int)(layerIndex) < config.numberOfBottomLayers && layerIndex % 2 == 1)
                    {
                        //Add extra insets every 2 layers when spiralizing, this makes bottoms of cups watertight.
                        insetCount += 5;
                    }

                    SliceLayer layer = storage.volumes[volumeIndex].layers[layerIndex];
                    int extrusionWidth = config.extrusionWidth_um;
                    if (layerIndex == 0)
                    {
                        extrusionWidth = config.firstLayerExtrusionWidth_um;
                    }
                    Inset.generateInsets(layer, extrusionWidth, insetCount);
                }
                LogOutput.log("Creating Insets {0}/{1}\n".FormatWith(layerIndex + 1, totalLayers));
            }

            if (config.wipeShieldDistanceFromShapes_um > 0)
            {
                CreateWipeShields(storage, totalLayers);
            }

            LogOutput.log("Generated inset in {0:0.0}s\n".FormatWith(timeKeeper.Elapsed.Seconds));
            timeKeeper.Restart();

            for (int layerIndex = 0; layerIndex < totalLayers; layerIndex++)
            {
                //Only generate bottom and top layers and infill for the first X layers when spiralize is choosen.
                if (!config.continuousSpiralOuterPerimeter || (int)(layerIndex) < config.numberOfBottomLayers)
                {
                    for (int volumeIndex = 0; volumeIndex < storage.volumes.Count; volumeIndex++)
                    {
                        int extrusionWidth = config.extrusionWidth_um;
                        if (layerIndex == 0)
                        {
                            extrusionWidth = config.firstLayerExtrusionWidth_um;
                        }

                        Skin.generateTopAndBottomLayers(layerIndex, storage.volumes[volumeIndex], extrusionWidth, config.numberOfBottomLayers, config.numberOfTopLayers);
                        Skin.generateSparse(layerIndex, storage.volumes[volumeIndex], extrusionWidth, config.numberOfBottomLayers, config.numberOfTopLayers);
                    }
                }
                LogOutput.log("Creating Top & Bottom Layers {0}/{1}\n".FormatWith(layerIndex + 1, totalLayers));
            }
            LogOutput.log("Generated top bottom layers in {0:0.0}s\n".FormatWith(timeKeeper.Elapsed.Seconds));
            timeKeeper.Restart();

            if (config.wipeTowerSize_um > 0)
            {
                Polygon p = new Polygon();
                storage.wipeTower.Add(p);
                p.Add(new IntPoint(storage.modelMin.x - 3000, storage.modelMax.y + 3000));
                p.Add(new IntPoint(storage.modelMin.x - 3000, storage.modelMax.y + 3000 + config.wipeTowerSize_um));
                p.Add(new IntPoint(storage.modelMin.x - 3000 - config.wipeTowerSize_um, storage.modelMax.y + 3000 + config.wipeTowerSize_um));
                p.Add(new IntPoint(storage.modelMin.x - 3000 - config.wipeTowerSize_um, storage.modelMax.y + 3000));

                storage.wipePoint = new IntPoint(storage.modelMin.x - 3000 - config.wipeTowerSize_um / 2, storage.modelMax.y + 3000 + config.wipeTowerSize_um / 2);
            }

            Skirt.generateSkirt(storage, config.skirtDistance_um, config.firstLayerExtrusionWidth_um, config.numberOfSkirtLoops, config.skirtMinLength_um, config.firstLayerThickness_um);
            if (config.enableRaft)
            {
                Raft.GenerateRaftOutlines(storage, config.raftExtraDistanceAroundPart_um);
            }

            for (int volumeIdx = 0; volumeIdx < storage.volumes.Count; volumeIdx++)
            {
                for (int layerNr = 0; layerNr < totalLayers; layerNr++)
                {
                    for (int partNr = 0; partNr < storage.volumes[volumeIdx].layers[layerNr].parts.Count; partNr++)
                    {
                        if (layerNr > 0)
                        {
                            storage.volumes[volumeIdx].layers[layerNr].parts[partNr].bridgeAngle = Bridge.bridgeAngle(storage.volumes[volumeIdx].layers[layerNr].parts[partNr], storage.volumes[volumeIdx].layers[layerNr - 1]);
                        }
                        else
                        {
                            storage.volumes[volumeIdx].layers[layerNr].parts[partNr].bridgeAngle = -1;
                        }
                    }
                }
            }
        }

        private void CreateWipeShields(SliceDataStorage storage, int totalLayers)
        {
            for (int layerNr = 0; layerNr < totalLayers; layerNr++)
            {
                Polygons wipeShield = new Polygons();
                for (int volumeIdx = 0; volumeIdx < storage.volumes.Count; volumeIdx++)
                {
                    for (int partNr = 0; partNr < storage.volumes[volumeIdx].layers[layerNr].parts.Count; partNr++)
                    {
                        wipeShield = wipeShield.CreateUnion(storage.volumes[volumeIdx].layers[layerNr].parts[partNr].outline.Offset(config.wipeShieldDistanceFromShapes_um));
                    }
                }
                storage.wipeShield.Add(wipeShield);
            }

            for (int layerIndex = 0; layerIndex < totalLayers; layerIndex++)
            {
                storage.wipeShield[layerIndex] = storage.wipeShield[layerIndex].Offset(-1000).Offset(1000);
            }

            int offsetAngle = (int)Math.Tan(60.0 * Math.PI / 180) * config.layerThickness_um;//Allow for a 60deg angle in the wipeShield.
            for (int layerNr = 1; layerNr < totalLayers; layerNr++)
            {
                storage.wipeShield[layerNr] = storage.wipeShield[layerNr].CreateUnion(storage.wipeShield[layerNr - 1].Offset(-offsetAngle));
            }

            for (int layerNr = totalLayers - 1; layerNr > 0; layerNr--)
            {
                storage.wipeShield[layerNr - 1] = storage.wipeShield[layerNr - 1].CreateUnion(storage.wipeShield[layerNr].Offset(-offsetAngle));
            }
        }

        void writeGCode(SliceDataStorage storage)
        {
            if (fileNr == 1)
            {
                if (gcode.GetOutputType() == ConfigConstants.OUTPUT_TYPE.ULTIGCODE)
                {
                    gcode.writeComment("TYPE:UltiGCode");
                    gcode.writeComment("TIME:<__TIME__>");
                    gcode.writeComment("MATERIAL:<FILAMENT>");
                    gcode.writeComment("MATERIAL2:<FILAMEN2>");
                }
                gcode.writeCode(config.startCode);
                if (gcode.GetOutputType() == ConfigConstants.OUTPUT_TYPE.BFB)
                {
                    gcode.writeComment("enable auto-retraction");
                    gcode.writeLine("M227 S{0} P{1}".FormatWith(config.retractionAmount_um * 2560 / 1000, config.retractionAmount_um * 2560 / 1000));
                }

            }
            else
            {
                gcode.writeFanCommand(0);
                gcode.resetExtrusionValue();
                gcode.writeRetraction();
                gcode.setZ(maxObjectHeight + 5000);
                gcode.writeMove(gcode.getPositionXY(), config.travelSpeed, 0);
                gcode.writeMove(new IntPoint(storage.modelMin.x, storage.modelMin.y), config.travelSpeed, 0);
            }
            fileNr++;

            int totalLayers = storage.volumes[0].layers.Count;
            gcode.writeComment("Layer count: {0}".FormatWith(totalLayers));

            // keep the raft generation code inside of raft
            Raft.GenerateRaftGCodeIfRequired(storage, config, gcode);

            int volumeIdx = 0;
            for (int layerIndex = 0; layerIndex < totalLayers; layerIndex++)
            {
                LogOutput.log("Writing Layers {0}/{1}\n".FormatWith(layerIndex + 1, totalLayers));

                LogOutput.logProgress("export", layerIndex + 1, totalLayers);

                int extrusionWidth_um = config.extrusionWidth_um;
                if (layerIndex == 0)
                {
                    extrusionWidth_um = config.firstLayerExtrusionWidth_um;
                }

                if (layerIndex == 0)
                {
                    skirtConfig.setData(config.firstLayerSpeed, extrusionWidth_um, "SKIRT");
                    inset0Config.setData(config.firstLayerSpeed, extrusionWidth_um, "WALL-OUTER");
                    insetXConfig.setData(config.firstLayerSpeed, extrusionWidth_um, "WALL-INNER");
                    fillConfig.setData(config.firstLayerSpeed, extrusionWidth_um, "FILL");
                }
                else
                {
                    skirtConfig.setData(config.supportMaterialSpeed, extrusionWidth_um, "SKIRT");
                    inset0Config.setData(config.outsidePerimeterSpeed, extrusionWidth_um, "WALL-OUTER");
                    insetXConfig.setData(config.insidePerimetersSpeed, extrusionWidth_um, "WALL-INNER");
                    fillConfig.setData(config.infillSpeed, extrusionWidth_um, "FILL");
                }
                supportConfig.setData(config.firstLayerSpeed, config.extrusionWidth_um, "SUPPORT");

                gcode.writeComment("LAYER:{0}".FormatWith(layerIndex));
                if (layerIndex == 0)
                {
                    gcode.setExtrusion(config.firstLayerThickness_um, config.filamentDiameter_um, config.extrusionMultiplier);
                }
                else
                {
                    gcode.setExtrusion(config.layerThickness_um, config.filamentDiameter_um, config.extrusionMultiplier);
                }

                GCodePlanner gcodeLayer = new GCodePlanner(gcode, config.travelSpeed, config.minimumTravelToCauseRetraction_um);

                // get the correct height for this layer
                int z = config.firstLayerThickness_um + layerIndex * config.layerThickness_um;
                if (config.enableRaft)
                {
                    z += config.raftBaseThickness_um + config.raftInterfaceThicknes_um + config.raftSurfaceLayers_um * config.raftSurfaceThickness_um;
                    if (layerIndex == 0)
                    {
                        // We only raise the first layer of the print up by the air gap.
                        // To give it:
                        //   Less press into the raft
                        //   More time to cool
                        //   more surface area to air while extruding
                        z += config.raftAirGap_um;
                    }
                }

                gcode.setZ(z);

                bool printSupportFirst = (storage.support.generated && config.supportExtruder > 0 && config.supportExtruder == gcodeLayer.getExtruder());
                if (printSupportFirst)
                {
                    addSupportToGCode(storage, gcodeLayer, layerIndex, config.extrusionWidth_um);
                }

                for (int volumeCnt = 0; volumeCnt < storage.volumes.Count; volumeCnt++)
                {
                    if (volumeCnt > 0)
                    {
                        volumeIdx = (volumeIdx + 1) % storage.volumes.Count;
                    }

                    addVolumeLayerToGCode(storage, gcodeLayer, volumeIdx, layerIndex, extrusionWidth_um);
                }

                if (!printSupportFirst)
                {
                    addSupportToGCode(storage, gcodeLayer, layerIndex, config.extrusionWidth_um);
                }

                //Finish the layer by applying speed corrections for minimum layer times.
                gcodeLayer.forceMinimumLayerTime(config.minimumLayerTimeSeconds, config.minimumPrintingSpeed);

                int fanSpeed = config.fanSpeedMinPercent;
                if (gcodeLayer.getExtrudeSpeedFactor() <= 50)
                {
                    fanSpeed = config.fanSpeedMaxPercent;
                }
                else
                {
                    int n = gcodeLayer.getExtrudeSpeedFactor() - 50;
                    fanSpeed = config.fanSpeedMinPercent * n / 50 + config.fanSpeedMaxPercent * (50 - n) / 50;
                }
                if ((int)(layerIndex) < config.firstLayerToAllowFan)
                {
                    // Don't allow the fan below this layer
                    fanSpeed = 0;
                }
                gcode.writeFanCommand(fanSpeed);

                gcodeLayer.writeGCode(config.doCoolHeadLift, (int)(layerIndex) > 0 ? config.layerThickness_um : config.firstLayerThickness_um);
            }

            LogOutput.log("Wrote layers in {0:0.00}s.\n".FormatWith(timeKeeper.Elapsed.Seconds));
            timeKeeper.Restart();
            gcode.tellFileSize();
            gcode.writeFanCommand(0);

            //Store the object height for when we are printing multiple objects, as we need to clear every one of them when moving to the next position.
            maxObjectHeight = Math.Max(maxObjectHeight, storage.modelSize.z);
        }

        //Add a single layer from a single mesh-volume to the GCode
        void addVolumeLayerToGCode(SliceDataStorage storage, GCodePlanner gcodeLayer, int volumeIdx, int layerNr, int extrusionWidth_um)
        {
            int prevExtruder = gcodeLayer.getExtruder();
            bool extruderChanged = gcodeLayer.setExtruder(volumeIdx);
            if (layerNr == 0 && volumeIdx == 0)
            {
                gcodeLayer.writePolygonsByOptimizer(storage.skirt, skirtConfig);
            }

            SliceLayer layer = storage.volumes[volumeIdx].layers[layerNr];
            if (extruderChanged)
            {
                addWipeTower(storage, gcodeLayer, layerNr, prevExtruder, extrusionWidth_um);
            }

            if (storage.wipeShield.Count > 0 && storage.volumes.Count > 1)
            {
                gcodeLayer.setAlwaysRetract(true);
                gcodeLayer.writePolygonsByOptimizer(storage.wipeShield[layerNr], skirtConfig);
                gcodeLayer.setAlwaysRetract(!config.avoidCrossingPerimeters);
            }

            PathOrderOptimizer partOrderOptimizer = new PathOrderOptimizer(gcode.getPositionXY());
            for (int partNr = 0; partNr < layer.parts.Count; partNr++)
            {
                partOrderOptimizer.addPolygon(layer.parts[partNr].insets[0][0]);
            }
            partOrderOptimizer.optimize();

            for (int partCounter = 0; partCounter < partOrderOptimizer.polyOrder.Count; partCounter++)
            {
                SliceLayerPart part = layer.parts[partOrderOptimizer.polyOrder[partCounter]];

                if (config.avoidCrossingPerimeters)
                {
                    gcodeLayer.setCombBoundary(part.combBoundery);
                }
                else
                {
                    gcodeLayer.setAlwaysRetract(true);
                }

                if (config.numberOfPerimeters > 0)
                {
                    if (config.continuousSpiralOuterPerimeter)
                    {
                        if ((int)(layerNr) >= config.numberOfBottomLayers)
                        {
                            inset0Config.spiralize = true;
                        }

                        if ((int)(layerNr) == config.numberOfBottomLayers && part.insets.Count > 0)
                        {
                            gcodeLayer.writePolygonsByOptimizer(part.insets[0], insetXConfig);
                        }
                    }

                    for (int insetNr = part.insets.Count - 1; insetNr > -1; insetNr--)
                    {
                        if (insetNr == 0)
                        {
                            gcodeLayer.writePolygonsByOptimizer(part.insets[insetNr], inset0Config);
                        }
                        else
                        {
                            gcodeLayer.writePolygonsByOptimizer(part.insets[insetNr], insetXConfig);
                        }
                    }
                }

                Polygons fillPolygons = new Polygons();
                int fillAngle = config.infillStartingAngle;
                if ((layerNr & 1) == 1)
                {
                    fillAngle += 90;
                }

                // generate infill for outline including bridging
                Infill.GenerateLinePaths(part.skinOutline, fillPolygons, extrusionWidth_um, extrusionWidth_um, config.infillExtendIntoPerimeter_um, (part.bridgeAngle > -1) ? part.bridgeAngle : fillAngle);

                // generate the infill for this part on this layer
                if (config.infillPercent > 0)
                {
                    switch (config.infillType)
                    {
                        case ConfigConstants.INFILL_TYPE.LINES:
                            Infill.GenerateLineInfill(config, part, fillPolygons, extrusionWidth_um, fillAngle);
                            break;

                        case ConfigConstants.INFILL_TYPE.GRID:
                            Infill.GenerateGridInfill(config, part, fillPolygons, extrusionWidth_um, fillAngle);
                            break;

                        default:
                            throw new NotImplementedException();
                    }
                }

                gcodeLayer.writePolygonsByOptimizer(fillPolygons, fillConfig);

                //After a layer part, make sure the nozzle is inside the comb boundary, so we do not retract on the perimeter.
                if (!config.continuousSpiralOuterPerimeter || (int)(layerNr) < config.numberOfBottomLayers)
                {
                    gcodeLayer.moveInsideCombBoundary(extrusionWidth_um * 2);
                }
            }
            gcodeLayer.setCombBoundary(null);
        }

        void addSupportToGCode(SliceDataStorage storage, GCodePlanner gcodeLayer, int layerIndex, int extrusionWidth_um)
        {
            if (!storage.support.generated)
            {
                return;
            }

            if (config.supportExtruder > -1)
            {
                int prevExtruder = gcodeLayer.getExtruder();
                if (gcodeLayer.setExtruder(config.supportExtruder))
                {
                    addWipeTower(storage, gcodeLayer, layerIndex, prevExtruder, extrusionWidth_um);
                }

                if (storage.wipeShield.Count > 0 && storage.volumes.Count == 1)
                {
                    gcodeLayer.setAlwaysRetract(true);
                    gcodeLayer.writePolygonsByOptimizer(storage.wipeShield[layerIndex], skirtConfig);
                    gcodeLayer.setAlwaysRetract(config.avoidCrossingPerimeters);
                }
            }

            int z = config.firstLayerThickness_um + layerIndex * config.layerThickness_um;
            SupportPolyGenerator supportGenerator = new SupportPolyGenerator(storage.support, z);
            for (int volumeIndex = 0; volumeIndex < storage.volumes.Count; volumeIndex++)
            {
                SliceLayer layer = storage.volumes[volumeIndex].layers[layerIndex];
                for (int partIndex = 0; partIndex < layer.parts.Count; partIndex++)
                {
                    supportGenerator.polygons = supportGenerator.polygons.CreateDifference(layer.parts[partIndex].outline.Offset(config.supportXYDistance_um));
                }
            }
            //Contract and expand the support polygons so small sections are removed and the final polygon is smoothed a bit.
            supportGenerator.polygons = supportGenerator.polygons.Offset(-extrusionWidth_um * 3);
            supportGenerator.polygons = supportGenerator.polygons.Offset(extrusionWidth_um * 3);

            List<Polygons> supportIslands = supportGenerator.polygons.SplitIntoParts();
            PathOrderOptimizer islandOrderOptimizer = new PathOrderOptimizer(gcode.getPositionXY());

            for (int islandIndex = 0; islandIndex < supportIslands.Count; islandIndex++)
            {
                islandOrderOptimizer.addPolygon(supportIslands[islandIndex][0]);
            }
            islandOrderOptimizer.optimize();

            for (int islandIndex = 0; islandIndex < supportIslands.Count; islandIndex++)
            {
                Polygons island = supportIslands[islandOrderOptimizer.polyOrder[islandIndex]];
                Polygons supportLines = new Polygons();
                if (config.supportLineSpacing_um > 0)
                {
                    switch (config.supportType)
                    {
                        case ConfigConstants.SUPPORT_TYPE.GRID:
                            if (config.supportLineSpacing_um > extrusionWidth_um * 4)
                            {
                                Infill.GenerateLinePaths(island, supportLines, extrusionWidth_um, config.supportLineSpacing_um * 2, config.infillExtendIntoPerimeter_um, 0);
                                Infill.GenerateLinePaths(island, supportLines, extrusionWidth_um, config.supportLineSpacing_um * 2, config.infillExtendIntoPerimeter_um, 90);
                            }
                            else
                            {
                                Infill.GenerateLinePaths(island, supportLines, extrusionWidth_um, config.supportLineSpacing_um, config.infillExtendIntoPerimeter_um, (layerIndex & 1) == 1 ? 0 : 90);
                            }
                            break;

                        case ConfigConstants.SUPPORT_TYPE.LINES:
                            Infill.GenerateLinePaths(island, supportLines, extrusionWidth_um, config.supportLineSpacing_um, config.infillExtendIntoPerimeter_um, 0);
                            break;
                    }
                }

                gcodeLayer.forceRetract();
                if (config.avoidCrossingPerimeters)
                {
                    gcodeLayer.setCombBoundary(island);
                }

                gcodeLayer.writePolygonsByOptimizer(island, supportConfig);
                gcodeLayer.writePolygonsByOptimizer(supportLines, supportConfig);
                gcodeLayer.setCombBoundary(null);
            }
        }

        void addWipeTower(SliceDataStorage storage, GCodePlanner gcodeLayer, int layerNr, int prevExtruder, int extrusionWidth_um)
        {
            if (config.wipeTowerSize_um == 1)
            {
                return;
            }

            //If we changed extruder, print the wipe/prime tower for this nozzle;
            gcodeLayer.writePolygonsByOptimizer(storage.wipeTower, supportConfig);
            Polygons fillPolygons = new Polygons();
            Infill.GenerateLinePaths(storage.wipeTower, fillPolygons, extrusionWidth_um, extrusionWidth_um, config.infillExtendIntoPerimeter_um, 45 + 90 * (layerNr % 2));
            gcodeLayer.writePolygonsByOptimizer(fillPolygons, supportConfig);

            //Make sure we wipe the old extruder on the wipe tower.
            gcodeLayer.writeTravel(storage.wipePoint - config.extruderOffsets[prevExtruder] + config.extruderOffsets[gcodeLayer.getExtruder()]);
        }
    }
}