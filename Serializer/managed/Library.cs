﻿namespace Microsoft.FrozenObjects
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.IO;
    using System.Reflection;
    using System.Reflection.Metadata;
    using System.Reflection.Metadata.Ecma335;
    using System.Reflection.PortableExecutable;
    using System.Runtime.CompilerServices;
    using System.Runtime.InteropServices;
    using static CallIndirectHelpers;

    public static class Serializer
    {
        public static unsafe void SerializeObject(object o, string outputDataPath, string outputAssemblyFilePath, string outputNamespace, string typeName, string methodName, Version version, Type methodType = null, byte[] privateKeyOpt = null)
        {
            var outputAssemblyName = Path.GetFileNameWithoutExtension(outputAssemblyFilePath);
            var outputModuleName = Path.GetFileName(outputAssemblyFilePath);

            IntPtr handle;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                handle = NativeLibrary.Load("Microsoft.FrozenObjects.Serializer.Native.dll");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                handle = NativeLibrary.Load("Microsoft.FrozenObjects.Serializer.Native.so");
            }
            else
            {
                throw new PlatformNotSupportedException();
            }

            IntPtr methodTableTokenTupleList;
            IntPtr methodTableTokenTupleListVecPtr;
            IntPtr methodTableTokenTupleListCount;
            IntPtr functionPointerFixupList;
            IntPtr functionPointerFixupListVecPtr;
            IntPtr outFunctionPointerFixupListCount;
            IntPtr stringPtr = IntPtr.Zero;

            try
            {
                stringPtr = Marshal.StringToHGlobalAnsi(outputDataPath);
                ManagedCallISerializeObject(o, stringPtr, IntPtr.Zero, out methodTableTokenTupleList, out methodTableTokenTupleListVecPtr, out methodTableTokenTupleListCount, out functionPointerFixupList, out functionPointerFixupListVecPtr, out outFunctionPointerFixupListCount, NativeLibrary.GetExport(handle, "SerializeObject"));
            }
            finally
            {
                if (stringPtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(stringPtr);
                }
            }

            Dictionary<Type, int> typeTokenMap;

            try
            {
                var span = new ReadOnlySpan<MethodTableToken>((void*)methodTableTokenTupleList, (int)methodTableTokenTupleListCount);
                typeTokenMap = new Dictionary<Type, int>();

                for (int i = 0; i < span.Length; ++i)
                {
                    var mt = span[i].MethodTable;
                    var tmp = &mt;
                    typeTokenMap.Add(Unsafe.Read<object>(&tmp).GetType(), (int)span[i].Token); // Meh, expected assert failure: !CREATE_CHECK_STRING(bSmallObjectHeapPtr || bLargeObjectHeapPtr) https://github.com/dotnet/coreclr/blob/476dc1cb88a0dcedd891a0ef7a2e05d5c2f94f68/src/vm/object.cpp#L611
                }
            }
            finally
            {
                StdCallICleanup(methodTableTokenTupleListVecPtr, functionPointerFixupListVecPtr, NativeLibrary.GetExport(handle, "Cleanup"));
            }

            var typeQueue = new Queue<Type>();

            foreach (var entry in typeTokenMap)
            {
                typeQueue.Enqueue(entry.Key);
            }

            var allTypes = new HashSet<Type>();
            var allAssemblies = new HashSet<Assembly>();

            while (typeQueue.Count != 0)
            {
                var type = typeQueue.Peek();
                if (!allTypes.Contains(type))
                {
                    if (type.IsPointer || type.IsByRef || type.IsByRefLike ||type.IsCOMObject) // || type.IsCollectible ??
                    {
                        throw new NotSupportedException();
                    }

                    if (type.IsArray)
                    {
                        typeQueue.Enqueue(type.GetElementType());
                    }
                    else
                    {
                        var declaringType = type.DeclaringType;
                        if (declaringType != null)
                        {
                            typeQueue.Enqueue(declaringType);
                        }

                        var baseType = type.BaseType;
                        if (baseType != null)
                        {
                            typeQueue.Enqueue(baseType);
                        }

                        if (type.IsGenericType)
                        {
                            var typeArgs = type.GenericTypeArguments;
                            for (int i = 0; i < typeArgs.Length; ++i)
                            {
                                typeQueue.Enqueue(typeArgs[i]);
                            }
                        }
                    }

                    var typeAssembly = type.Assembly;
                    if (!allAssemblies.Contains(typeAssembly))
                    {
                        allAssemblies.Add(typeAssembly);
                    }

                    allTypes.Add(type);
                }

                typeQueue.Dequeue();
            }

            var metadataBuilder = new MetadataBuilder();
            metadataBuilder.AddModule(0, metadataBuilder.GetOrAddString(outputModuleName), metadataBuilder.GetOrAddGuid(Guid.NewGuid()), default, default);
            metadataBuilder.AddAssembly(metadataBuilder.GetOrAddString(outputAssemblyName), version, default, default, default, AssemblyHashAlgorithm.Sha1);

            var assemblyReferenceHandleMap = new Dictionary<Assembly, AssemblyReferenceHandle>();

            foreach (var assembly in allAssemblies)
            {
                var assemblyName = assembly.GetName();
                var assemblyNameStringHandle = metadataBuilder.GetOrAddString(assemblyName.Name);

                var publicKeyTokenBlobHandle = default(BlobHandle);
                var publicKeyToken = assemblyName.GetPublicKeyToken();
                if (publicKeyToken != null)
                {
                    publicKeyTokenBlobHandle = metadataBuilder.GetOrAddBlob(publicKeyToken);
                }

                StringHandle cultureStringHandle = default;
                if (assemblyName.CultureName != null)
                {
                    cultureStringHandle = metadataBuilder.GetOrAddString(assemblyName.CultureName);
                }

                assemblyReferenceHandleMap.Add(assembly, metadataBuilder.AddAssemblyReference(assemblyNameStringHandle, assemblyName.Version, cultureStringHandle, publicKeyTokenBlobHandle, default, default));
            }

            var uniqueTypeRefMap = new Dictionary<Type, EntityHandle>();

            foreach (var type in allTypes)
            {
                var typeList = new List<Type> { type };
                {
                    var tmp = type;
                    while (tmp.DeclaringType != null)
                    {
                        typeList.Add(tmp.DeclaringType);
                        tmp = tmp.DeclaringType;
                    }
                }

                for (int i = typeList.Count - 1; i > -1; --i)
                {
                    var t = typeList[i];
                    if (!uniqueTypeRefMap.ContainsKey(t))
                    {
                        var declaringType = t.DeclaringType;
                        var resolutionScope = declaringType == null ? assemblyReferenceHandleMap[t.Assembly] : uniqueTypeRefMap[declaringType];

                        var @namespace = default(StringHandle);
                        if (declaringType == null && !string.IsNullOrEmpty(t.Namespace))
                        {
                            @namespace = metadataBuilder.GetOrAddString(t.Namespace);
                        }

                        uniqueTypeRefMap.Add(t, metadataBuilder.AddTypeReference(resolutionScope, @namespace, metadataBuilder.GetOrAddString(t.Name)));
                    }
                }
            }

            var primitiveTypeCodeMap = new Dictionary<Type, PrimitiveTypeCode>
            {
                { typeof(bool), PrimitiveTypeCode.Boolean },
                { typeof(char), PrimitiveTypeCode.Char },
                { typeof(sbyte), PrimitiveTypeCode.SByte },
                { typeof(byte), PrimitiveTypeCode.Byte },
                { typeof(short), PrimitiveTypeCode.Int16 },
                { typeof(ushort), PrimitiveTypeCode.UInt16 },
                { typeof(int), PrimitiveTypeCode.Int32 },
                { typeof(uint), PrimitiveTypeCode.UInt32 },
                { typeof(long), PrimitiveTypeCode.Int64 },
                { typeof(ulong), PrimitiveTypeCode.UInt64 },
                { typeof(float), PrimitiveTypeCode.Single },
                { typeof(double), PrimitiveTypeCode.Double },
                { typeof(string), PrimitiveTypeCode.String },
                { typeof(IntPtr), PrimitiveTypeCode.IntPtr }, // We don't really want these, except for that custom MethodInfo support
                { typeof(UIntPtr), PrimitiveTypeCode.UIntPtr }, // We don't really want these, except for that custom MethodInfo support
                { typeof(object), PrimitiveTypeCode.Object }
            };

            var typeToTypeSpecMap = new Dictionary<Type, EntityHandle>();
            foreach (var type in typeTokenMap.Keys)
            {
                var blobBuilder = new BlobBuilder();
                var encoder = new SignatureTypeEncoder(blobBuilder);

                HandleType(type, ref encoder, primitiveTypeCodeMap, uniqueTypeRefMap);

                typeToTypeSpecMap.Add(type, metadataBuilder.AddTypeSpecification(metadataBuilder.GetOrAddBlob(blobBuilder)));
            }

            var netstandardAssemblyRef = metadataBuilder.AddAssemblyReference(metadataBuilder.GetOrAddString("netstandard"), new Version(2, 0, 0, 0), default, metadataBuilder.GetOrAddBlob(new byte[] { 0xCC, 0x7B, 0x13, 0xFF, 0xCD, 0x2D, 0xDD, 0x51 }), default, default);
            var systemObjectTypeRef = metadataBuilder.AddTypeReference(netstandardAssemblyRef, metadataBuilder.GetOrAddString("System"), metadataBuilder.GetOrAddString("Object"));

            var frozenObjectSerializerAssemblyRef = metadataBuilder.AddAssemblyReference(
                name: metadataBuilder.GetOrAddString("Microsoft.FrozenObjects"),
                version: new Version(1, 0, 0, 0),
                culture: default,
                publicKeyOrToken: default,
                flags: default,
                hashValue: default);

            var runtimeTypeHandleObjectRef = metadataBuilder.AddTypeReference(
                netstandardAssemblyRef,
                metadataBuilder.GetOrAddString("System"),
                metadataBuilder.GetOrAddString("RuntimeTypeHandle"));

            var deserializerRef = metadataBuilder.AddTypeReference(
                frozenObjectSerializerAssemblyRef,
                metadataBuilder.GetOrAddString("Microsoft.FrozenObjects"),
                metadataBuilder.GetOrAddString("Deserializer"));

            var ilBuilder = new BlobBuilder();

            var frozenObjectDeserializerSignature = new BlobBuilder();

            new BlobEncoder(frozenObjectDeserializerSignature).
                MethodSignature().
                Parameters(2,
                    returnType => returnType.Type().Object(),
                    parameters =>
                    {
                        parameters.AddParameter().Type().SZArray().Type(runtimeTypeHandleObjectRef, true);
                        parameters.AddParameter().Type().String();
                    });

            var deserializeMemberRef = metadataBuilder.AddMemberReference(
                deserializerRef,
                metadataBuilder.GetOrAddString("Deserialize"),
                metadataBuilder.GetOrAddBlob(frozenObjectDeserializerSignature));

            var mainSignature = new BlobBuilder();

            new BlobEncoder(mainSignature).
                MethodSignature().
                Parameters(1, returnType => returnType.Type().Object(), parameters =>
                {
                    parameters.AddParameter().Type().String();
                });

            var codeBuilder = new BlobBuilder();

            var il = new InstructionEncoder(codeBuilder);
            il.LoadConstantI4(typeTokenMap.Count);
            il.OpCode(ILOpCode.Newarr);
            il.Token(runtimeTypeHandleObjectRef);

            foreach (var entry in typeTokenMap)
            {
                il.OpCode(ILOpCode.Dup);
                il.LoadConstantI4(entry.Value);
                il.OpCode(ILOpCode.Ldtoken);
                il.Token(typeToTypeSpecMap[entry.Key]);
                il.OpCode(ILOpCode.Stelem);
                il.Token(runtimeTypeHandleObjectRef);
            }

            il.LoadArgument(0);
            il.OpCode(ILOpCode.Call);
            il.Token(deserializeMemberRef);
            il.OpCode(ILOpCode.Ret);

            var methodBodyStream = new MethodBodyStreamEncoder(ilBuilder);

            int mainBodyOffset = methodBodyStream.AddMethodBody(il);
            codeBuilder.Clear();

            var mainMethodDef = metadataBuilder.AddMethodDefinition(
                            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
                            MethodImplAttributes.IL | MethodImplAttributes.Managed,
                            metadataBuilder.GetOrAddString(methodName),
                            metadataBuilder.GetOrAddBlob(mainSignature),
                            mainBodyOffset,
                            parameterList: default);

            metadataBuilder.AddTypeDefinition(
                default,
                default,
                metadataBuilder.GetOrAddString("<Module>"),
                baseType: default,
                fieldList: MetadataTokens.FieldDefinitionHandle(1),
                methodList: mainMethodDef);

            metadataBuilder.AddTypeDefinition(
                TypeAttributes.Class | TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.AutoLayout | TypeAttributes.AnsiClass | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
                metadataBuilder.GetOrAddString(outputNamespace),
                metadataBuilder.GetOrAddString(typeName),
                systemObjectTypeRef,
                fieldList: MetadataTokens.FieldDefinitionHandle(1),
                methodList: mainMethodDef);

            using (var fs = new FileStream(outputAssemblyFilePath, FileMode.Create, FileAccess.Write))
            {
                WritePEImage(fs, metadataBuilder, ilBuilder, privateKeyOpt);
            }
        }

        private static void HandleSZArray(Type type, ref SignatureTypeEncoder encoder, Dictionary<Type, PrimitiveTypeCode> primitiveTypeCodeMap, Dictionary<Type, EntityHandle> uniqueTypeRefMap)
        {
            encoder.SZArray();
            HandleType(type.GetElementType(), ref encoder, primitiveTypeCodeMap, uniqueTypeRefMap);
        }

        private static void HandleArray(Type type, ref SignatureTypeEncoder encoder, Dictionary<Type, PrimitiveTypeCode> primitiveTypeCodeMap, Dictionary<Type, EntityHandle> uniqueTypeRefMap)
        {
            encoder.Array(out var elementTypeEncoder, out var arrayShapeEncoder);
            HandleType(type.GetElementType(), ref elementTypeEncoder, primitiveTypeCodeMap, uniqueTypeRefMap);

            var rank = type.GetArrayRank();
            var arr = new int[rank];
            var imm = ImmutableArray.Create(arr);

            arrayShapeEncoder.Shape(rank, ImmutableArray<int>.Empty, imm); // just so we match what the C# compiler generates for mdarrays
        }

        private static void HandleGenericInst(Type type, ref SignatureTypeEncoder encoder, Dictionary<Type, PrimitiveTypeCode> primitiveTypeCodeMap, Dictionary<Type, EntityHandle> uniqueTypeRefMap)
        {
            var genericTypeArguments = type.GenericTypeArguments;
            var genericTypeArgumentsEncoder = encoder.GenericInstantiation(uniqueTypeRefMap[type], genericTypeArguments.Length, type.IsValueType);

            for (int i = 0; i < genericTypeArguments.Length; ++i)
            {
                var genericTypeArgumentEncoder = genericTypeArgumentsEncoder.AddArgument();
                HandleType(genericTypeArguments[i], ref genericTypeArgumentEncoder, primitiveTypeCodeMap, uniqueTypeRefMap);
            }
        }

        /**
         * Type ::=
         *   BOOLEAN | CHAR | I1 | U1 | I2 | U2 | I4 | U4 | I8 | U8 | R4 | R8 | I | U | OBJECT | STRING
         * | ARRAY Type ArrayShape
         * | SZARRAY Type
         * | GENERICINST (CLASS | VALUETYPE) TypeRefOrSpecEncoded GenArgCount Type*
         * | (CLASS | VALUETYPE) TypeRefOrSpecEncoded
         */
        private static void HandleType(Type type, ref SignatureTypeEncoder encoder, Dictionary<Type, PrimitiveTypeCode> primitiveTypeCodeMap, Dictionary<Type, EntityHandle> uniqueTypeRefMap)
        {
            if (primitiveTypeCodeMap.TryGetValue(type, out var primitiveTypeCode))
            {
                // BOOLEAN | CHAR | I1 | U1 | I2 | U2 | I4 | U4 | I8 | U8 | R4 | R8 | I | U | OBJECT | STRING
                encoder.PrimitiveType(primitiveTypeCode);
            }
            else if (type.IsVariableBoundArray)
            {
                // ARRAY Type ArrayShape
                HandleArray(type, ref encoder, primitiveTypeCodeMap, uniqueTypeRefMap);
            }
            else if (type.IsSZArray)
            {
                // SZARRAY Type
                HandleSZArray(type, ref encoder, primitiveTypeCodeMap, uniqueTypeRefMap);
            }
            else if (type.IsConstructedGenericType)
            {
                // GENERICINST (CLASS | VALUETYPE) TypeRefOrSpecEncoded GenArgCount Type*
                HandleGenericInst(type, ref encoder, primitiveTypeCodeMap, uniqueTypeRefMap);
            }
            else if (type.IsPointer || type.IsCollectible || type.IsCOMObject)
            {
                // what other things can be on the heap but are not caught by this check?
                throw new NotSupportedException();
            }
            else
            {
                // CLASS TypeRefOrSpecEncoded | VALUETYPE TypeRefOrSpecEncoded
                encoder.Type(uniqueTypeRefMap[type], type.IsValueType);
            }
        }

        private static void WritePEImage(Stream peStream, MetadataBuilder metadataBuilder, BlobBuilder ilBuilder, byte[] privateKeyOpt, Blob mvidFixup = default)
        {
            var peBuilder = new ManagedPEBuilder(new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll), new MetadataRootBuilder(metadataBuilder), ilBuilder, entryPoint: default, flags: CorFlags.ILOnly | (privateKeyOpt != null ? CorFlags.StrongNameSigned : 0), deterministicIdProvider: null);

            var peBlob = new BlobBuilder();

            var contentId = peBuilder.Serialize(peBlob);

            if (!mvidFixup.IsDefault)
            {
                new BlobWriter(mvidFixup).WriteGuid(contentId.Guid);
            }

            peBlob.WriteContentTo(peStream);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MethodTableToken
        {
            public readonly IntPtr MethodTable;
            public readonly IntPtr Token;
        }
    }
}