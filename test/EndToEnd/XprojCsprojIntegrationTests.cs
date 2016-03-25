// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.IO;
using Microsoft.DotNet.Cli.Utils;
using Microsoft.DotNet.Tools.Test.Utilities;
using Xunit;

namespace Microsoft.DotNet.Tests.EndToEnd
{
    public class XprojCsprojIntegrationTests : TestBase
    {
        private static string XprojCsprojProjects => Path.Combine(RepoRoot, "TestAssets", "AsIsProjects", "XprojCsprojProjects");

        [Fact]
        public void BasicSolutionBuilds()
        {
            var testProjectDirectory = CreateTestProjectFrom("BasicProject_valid");
            var projectRoot = Path.Combine(testProjectDirectory.Path, "Sample1-Xproj+Csproj", "src", "ConsoleApp13");

            BuildProject(projectRoot, noDependencies: true);
        }

        private CommandResult BuildProject(string projectFile, bool noDependencies = false, bool expectFailure = false)
        {
            var buildCommand = new BuildCommand(projectFile, noDependencies: noDependencies);
            var result = buildCommand.ExecuteWithCapturedOutput();

            if (expectFailure)
            {
                result.Should().Fail();
            }
            else
            {
                result.Should().Pass();
            }

            return result;
        }

        private TempDirectory CreateTestProjectFrom(string projectName)
        {
            var dir = Temp.CreateDirectory();
            var projectPath = GetProjectPath(projectName);
            return dir.CopyDirectory(projectPath);
        }

        private static string GetProjectPath(string projectName)
        {
            return Path.Combine(XprojCsprojProjects, projectName);
        }
    }

}
