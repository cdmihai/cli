// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.DotNet.Cli.Utils;
using Microsoft.DotNet.Tools.Test.Utilities;
using Xunit;

namespace Microsoft.DotNet.Tools.Builder.Tests
{
    public class IncrementalTestsOnResources : IncrementalTestBase
    {

        public IncrementalTestsOnResources() : base(
            Path.Combine("TestProjects", "TestProjectWithResource"),
            "TestProjectWithResource",
            "Hello World!" + Environment.NewLine)
        {
        }

        [Fact]
        public void TestSecondBuildSkipsCompilationOnNonCultureResource()
        {
            var buildResult = BuildProject();
            AssertProjectCompiled(_mainProject, buildResult);

            buildResult = BuildProject();
            AssertProjectSkipped(_mainProject, buildResult);
        }
        
    }
}