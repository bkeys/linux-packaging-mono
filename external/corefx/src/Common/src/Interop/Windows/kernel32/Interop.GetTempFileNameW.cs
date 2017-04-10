// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Text;
using System.Runtime.InteropServices;

partial class Interop
{
    partial class Kernel32
    {
        [DllImport(Libraries.Kernel32, CharSet = CharSet.Unicode, SetLastError = true, BestFitMapping = false)]
        internal static extern uint GetTempFileNameW(string tmpPath, string prefix, uint uniqueIdOrZero, [Out]StringBuilder tmpFileName);
    }
}
