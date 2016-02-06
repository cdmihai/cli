// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.DotNet.Tools.Compiler;

namespace Microsoft.DotNet.Tools.Build
{
    internal class BuilderCommandApp : CompilerCommandApp
    {
        public const string BuildProfileFlag = "--build-profile";
        public const string ForceUnsafeFlag = "--no-incremental";
        public const string NoDependenciesFlag = "--no-dependencies";

        public bool BuildProfileValue => OptionHasValue(BuildProfileFlag);
        public bool ForceUnsafeValue => OptionHasValue(ForceUnsafeFlag);
        public bool NoDependenciesValue => OptionHasValue(NoDependenciesFlag);

        public BuilderCommandApp(string name, string fullName, string description) : base(name, fullName, description)
        {
            AddNoValueOption(BuildProfileFlag, "Set this flag to print the incremental safety checks that prevent incremental compilation");
            AddNoValueOption(ForceUnsafeFlag, "Set this flag to turn off incremental build");
            AddNoValueOption(NoDependenciesFlag, "Set this flag to ignore project to project references and only build the root project");
        }
    }
}