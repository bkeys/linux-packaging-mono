﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using Internal.TypeSystem;
using System.Diagnostics;

namespace Internal.Runtime.TypeLoader
{
    /// <summary>
    /// Useable when we have runtime EEType structures. Can represent the field layout necessary 
    /// to represent the size/alignment of the overall type, but must delegate to either NativeLayoutFieldAlgorithm
    /// or MetadataFieldLayoutAlgorithm to get information about individual fields.
    /// </summary>
    internal class NoMetadataFieldLayoutAlgorithm : FieldLayoutAlgorithm
    {
        private MetadataFieldLayoutAlgorithm _metadataFieldLayoutAlgorithm = new MetadataFieldLayoutAlgorithm();
        private static NativeLayoutFieldAlgorithm s_nativeLayoutFieldAlgorithm = new NativeLayoutFieldAlgorithm();

        public unsafe override bool ComputeContainsGCPointers(DefType type)
        {
            return type.RuntimeTypeHandle.ToEETypePtr()->HasGCPointers;
        }

        /// <summary>
        /// Reads the minimal information about type layout encoded in the 
        /// EEType. That doesn't include field information.
        /// </summary>
        public unsafe override ComputedInstanceFieldLayout ComputeInstanceLayout(DefType type, InstanceLayoutKind layoutKind)
        {
            // If we need the field information, delegate to the native layout algorithm or metadata algorithm
            if (layoutKind != InstanceLayoutKind.TypeOnly)
            {
                if (type.HasNativeLayout)
                    return s_nativeLayoutFieldAlgorithm.ComputeInstanceLayout(type, layoutKind);
                else
                    return _metadataFieldLayoutAlgorithm.ComputeInstanceLayout(type, layoutKind);
            }

            type.RetrieveRuntimeTypeHandleIfPossible();
            Debug.Assert(!type.RuntimeTypeHandle.IsNull());
            EEType* eeType = type.RuntimeTypeHandle.ToEETypePtr();

            ComputedInstanceFieldLayout layout = new ComputedInstanceFieldLayout()
            {
                ByteCountAlignment = IntPtr.Size,
                ByteCountUnaligned = eeType->IsInterface ? IntPtr.Size : checked((int)eeType->FieldByteCountNonGCAligned),
                FieldAlignment = eeType->FieldAlignmentRequirement,
                Offsets = (layoutKind == InstanceLayoutKind.TypeOnly) ? null : Array.Empty<FieldAndOffset>(), // No fields in EETypes
                PackValue = 0, // This isn't explicitly encoded, though FieldSize should take it into account
                // TODO, as we add more metadata handling logic, find out if its necessary.
            };

            if (eeType->IsValueType)
            {
                int valueTypeSize = checked((int)eeType->ValueTypeSize);
                layout.FieldSize = valueTypeSize;
            }
            else
            {
                layout.FieldSize = IntPtr.Size;
            }

            if ((eeType->RareFlags & EETypeRareFlags.RequiresAlign8Flag) == EETypeRareFlags.RequiresAlign8Flag)
            {
                layout.ByteCountAlignment = 8;
            }

            return layout;
        }

        public override ComputedStaticFieldLayout ComputeStaticFieldLayout(DefType type, StaticLayoutKind layoutKind)
        {
            // We can only reach this for pre-created types where we actually need field information
            // In that case, fall through to one of the other field layout algorithms.
            if (type.HasNativeLayout)
                return s_nativeLayoutFieldAlgorithm.ComputeStaticFieldLayout(type, layoutKind);
            else if (type is MetadataType)
                return _metadataFieldLayoutAlgorithm.ComputeStaticFieldLayout(type, layoutKind);

            // No statics information available
            ComputedStaticFieldLayout staticLayout = new ComputedStaticFieldLayout()
            {
                GcStatics = default(StaticsBlock),
                NonGcStatics = default(StaticsBlock),
                Offsets = Array.Empty<FieldAndOffset>(), // No fields are considered to exist for completely NoMetadataTypes
                ThreadStatics = default(StaticsBlock),
            };
            return staticLayout;
        }

        public override DefType ComputeHomogeneousFloatAggregateElementType(DefType type)
        {
            if (type.Context.Target.Architecture == TargetArchitecture.ARM)
            {
                unsafe
                {
                    // On ARM, the HFA type is encoded into the EEType directly
                    type.RetrieveRuntimeTypeHandleIfPossible();
                    Debug.Assert(!type.RuntimeTypeHandle.IsNull());
                    EEType* eeType = type.RuntimeTypeHandle.ToEETypePtr();
                    if (!eeType->IsHFA)
                        return null;

                    if (eeType->RequiresAlign8)
                        return type.Context.GetWellKnownType(WellKnownType.Double);
                    else
                        return type.Context.GetWellKnownType(WellKnownType.Single);
                }
            }
            else
            {
                // We must delegate to algorithms that can work off of a sort of metadata
                if (type.HasNativeLayout)
                    return s_nativeLayoutFieldAlgorithm.ComputeHomogeneousFloatAggregateElementType(type);
                else if (type is MetadataType)
                    return _metadataFieldLayoutAlgorithm.ComputeHomogeneousFloatAggregateElementType(type);
                else
                    return null; // If there isn't any form of metadata, it can't matter... as HFA is not part of the ABI except on ARM
            }
        }

        public override ValueTypeShapeCharacteristics ComputeValueTypeShapeCharacteristics(DefType type)
        {
            if (type.Context.Target.Architecture == TargetArchitecture.ARM)
            {
                unsafe
                {
                    // On ARM, the HFA type is encoded into the EEType directly
                    type.RetrieveRuntimeTypeHandleIfPossible();
                    Debug.Assert(!type.RuntimeTypeHandle.IsNull());
                    EEType* eeType = type.RuntimeTypeHandle.ToEETypePtr();
                    if (eeType->IsHFA)
                        return ValueTypeShapeCharacteristics.HomogenousFloatAggregate;
                    else
                        return ValueTypeShapeCharacteristics.None;
                }
            }
            else
            {
                // We must delegate to algorithms that can work off of a sort of metadata
                if (type.HasNativeLayout)
                    return s_nativeLayoutFieldAlgorithm.ComputeValueTypeShapeCharacteristics(type);
                else if (type is MetadataType)
                    return _metadataFieldLayoutAlgorithm.ComputeValueTypeShapeCharacteristics(type);
                else
                    return ValueTypeShapeCharacteristics.None; // If there isn't any form of metadata, it can't matter... as HFA is not part of the ABI except on ARM
            }
        }
    }
}
