// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace Microsoft.DotNet.ProjectModel.Graph
{
    public static class LockFilePatcher
    {
        public static LockFile PatchLockFile(string masterLockFilePath, IEnumerable<string> fragmentLockFilesPaths)
        {
            var masterLockFile = LockFileReader.Read(masterLockFilePath);

            return PatchLockFile(masterLockFile, fragmentLockFilesPaths);
        }

        public static LockFile PatchLockFile(LockFile masterLockFile, IEnumerable<string> fragmentLockFilesPaths)
        {
            var fragmentLockFiles = fragmentLockFilesPaths.Select(LockFileReader.Read);

            foreach (var fragmentLockFile in fragmentLockFiles)
            {
                masterLockFile.MergeWith(fragmentLockFile);
            }

            return masterLockFile;
        }
    }
}
