﻿/*
Copyright (c) 2014, Lars Brubaker
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met: 

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer. 
2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution. 

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR
ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

The views and conclusions contained in the software and documentation are those
of the authors and should not be interpreted as representing official policies, 
either expressed or implied, of the FreeBSD Project.
*/

using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using MatterSlice.ClipperLib;
using MatterHackers.MatterSlice;
using NUnit.Framework;

namespace MatterHackers.MatterSlice.Tests
{
    [TestFixture]
    public class SlicerLayerTests
    {
        [Test]
        public void WindingDirectionDoseNotMatter()
        {
            string manifoldFile = "../../SampleSTLs/20mm-box.stl";
            string manifoldGCode = Path.ChangeExtension(Path.GetFileName(manifoldFile), "gocde");
            string nonManifoldFile = "../../SampleSTLs/20mm-box bad winding.stl";
            string nonManifoldGCode = Path.ChangeExtension(Path.GetFileName(nonManifoldFile), "gocde");

            {
                // load a model that is correctly manifold
                ConfigSettings config = new ConfigSettings();
                fffProcessor processor = new fffProcessor(config);
                processor.setTargetFile(manifoldGCode);
                processor.LoadStlFile(manifoldFile);
                // slice and save it
                processor.DoProcessing();
                processor.finalize();
            }

            {
                // load a model that has some faces pointing the wroing way
                ConfigSettings config = new ConfigSettings();
                fffProcessor processor = new fffProcessor(config);
                processor.setTargetFile(nonManifoldGCode);
                processor.LoadStlFile(nonManifoldFile);
                // slice and save it
                processor.DoProcessing();
                processor.finalize();
            }

            // load both gcode files and check that they are the same
            string manifoldGCodeContent = File.ReadAllText(manifoldGCode);
            string nonManifoldGCodeContent = File.ReadAllText(nonManifoldGCode);
            Assert.AreEqual(manifoldGCodeContent, nonManifoldGCodeContent);
        }
    }

    public static class SlicingTests
    {
        static bool ranTests = false;

        public static bool RanTests { get { return ranTests; } }
        public static void Run()
        {
            if (!ranTests)
            {
                SlicerLayerTests slicerLayerTests = new SlicerLayerTests();
                slicerLayerTests.WindingDirectionDoseNotMatter();

                ranTests = true;
            }
        }
    }
}