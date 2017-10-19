﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Runtime.Serialization;

namespace System.Drawing.Imaging
{
    [Serializable]
    partial class Metafile
    {
        private Metafile(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }
}
