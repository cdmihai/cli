﻿// Copyright (c) .NET Foundation and contributors. All rights reserved.
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
    public class IncrementalTestBase : TestBase
    {
        protected readonly TempDirectory TempProjectRoot;

        protected readonly string TestProjectsRoot;
        protected readonly string MainProject;
        protected readonly string ExpectedOutput;

        public IncrementalTestBase(string testProjectsRoot, string mainProject, string expectedOutput)
        {
            TestProjectsRoot = testProjectsRoot;
            MainProject = mainProject;
            ExpectedOutput = expectedOutput;

            // create unique directories in the 'temp' folder
            var root = Temp.CreateDirectory();

            // recursively copy projects to the temp dir and restore them
            TempProjectRoot = root.CopyDirectory(testProjectsRoot);
            RunRestore(TempProjectRoot.Path);
        }

        protected void TouchSourcesOfProject()
        {
            TouchSourcesOfProject(MainProject);
        }

        protected void TouchSourcesOfProject(string projectToTouch)
        {
            foreach (var sourceFile in GetSourceFilesForProject(projectToTouch))
            {
                TouchFile(sourceFile);
            }
        }

        protected static void TouchFile(string file)
        {
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow);
        }

        protected CommandResult BuildProject(bool forceIncrementalUnsafe = false, bool expectBuildFailure = false)
        {
            var outputDir = GetBinRoot();
            var intermediateOutputDir = Path.Combine(Directory.GetParent(outputDir).FullName, "obj", MainProject);
            var mainProjectFile = GetProjectFile(MainProject);

            var buildCommand = new BuildCommand(mainProjectFile, output: outputDir, tempOutput: intermediateOutputDir ,forceIncrementalUnsafe : forceIncrementalUnsafe);
            var result = buildCommand.ExecuteWithCapturedOutput();

            if (!expectBuildFailure)
            {
                result.Should().Pass();
                TestOutputExecutable(outputDir, buildCommand.GetOutputExecutableName(), ExpectedOutput);
            }
            else
            {
                result.Should().Fail();
            }

            return result;
        }

        protected static void AssertProjectSkipped(string skippedProject, CommandResult buildResult)
        {
            Assert.Contains($"Project {skippedProject} was previously compiled. Skipping compilation.", buildResult.StdOut, StringComparison.OrdinalIgnoreCase);
        }

        protected static void AssertProjectCompiled(string rebuiltProject, CommandResult buildResult)
        {
            Assert.Contains($"Project {rebuiltProject} will be compiled", buildResult.StdOut, StringComparison.OrdinalIgnoreCase);
        }

        protected string GetBinRoot()
        {
            return Path.Combine(TempProjectRoot.Path, "bin");
        }

        protected virtual string GetProjectDirectory(string projectName)
        {
            return Path.Combine(TempProjectRoot.Path);
        }

        protected string GetProjectFile(string projectName)
        {
            return Path.Combine(GetProjectDirectory(projectName), "project.json");
        }

        private string GetOutputFileForProject(string projectName)
        {
            return Path.Combine(GetCompilationOutputPath(), projectName + ".dll");
        }

        private IEnumerable<string> GetSourceFilesForProject(string projectName)
        {
            return Directory.EnumerateFiles(GetProjectDirectory(projectName)).
                Where(f => f.EndsWith(".cs"));
        }

        protected string GetCompilationOutputPath()
        {
            var executablePath = Path.Combine(GetBinRoot(), "Debug", "dnxcore50");

            return executablePath;
        }

        private void RunRestore(string args)
        {
            var restoreCommand = new RestoreCommand();
            restoreCommand.Execute($"--quiet {args}").Should().Pass();
        }
    }
}