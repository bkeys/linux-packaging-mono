// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

internal class HeaderInfo
{
    internal readonly bool IsRequestRestricted;
    internal readonly bool IsResponseRestricted;
    //
    // Note that the HeaderName field is not always valid, and should not
    // be used after initialization. In particular, the HeaderInfo returned
    // for an unknown header will not have the correct header name.
    //
    internal readonly string HeaderName;
    internal readonly bool AllowMultiValues;

    internal HeaderInfo(string name, bool requestRestricted, bool responseRestricted, bool multi)
    {
        HeaderName = name;
        IsRequestRestricted = requestRestricted;
        IsResponseRestricted = responseRestricted;
        AllowMultiValues = multi;
    }
}