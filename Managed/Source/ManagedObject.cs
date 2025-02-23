using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace DotOther.Managed {

	using static DotOtherHost;

	internal enum ManagedType {
		Unknown,

		SByte,
		Byte,
		Short,
		UShort,
		Int,
		UInt,
		Long,
		ULong,

		Float,
		Double,

		Bool,

		Pointer
	};

#nullable enable
	internal static class ManagedObject {

		public readonly struct MethodKey : IEquatable<MethodKey> {
			public readonly string type_name;
			public readonly string name;
			public readonly ManagedType[] arg_types;
			public readonly Int32 param_count;

			public MethodKey(string type_name, string name, ManagedType[] arg_types, Int32 param_count) {
				this.type_name = type_name;
				this.name = name;
				this.arg_types = arg_types;
				this.param_count = arg_types.Length;
			}
			public override bool Equals([NotNullWhen(true)] object? obj) => obj is MethodKey key && Equals(key);

			bool IEquatable<MethodKey>.Equals(MethodKey other) {
				if (type_name != other.type_name || name != other.name || param_count != other.param_count) {
					return false;
				}

				for (Int32 i = 0; i < param_count; i++) {
					if (arg_types[i] != other.arg_types[i]) {
						return false;
					}
				}

				return true;
			}

			public override int GetHashCode() {
				return base.GetHashCode();
			}
		}

		internal static Dictionary<MethodKey , MethodInfo> methods = new Dictionary<MethodKey, MethodInfo>();

		private static unsafe MethodInfo? TryGetMethodInfo(Type type, string? name, ManagedType* types, Int32 count, BindingFlags flags) {
			MethodInfo? minfo = null;

			ManagedType[]? param_types = new ManagedType[count];

			unsafe {
				fixed (ManagedType* param_types_ptr = param_types) {
					ulong size = sizeof(ManagedType) * (ulong)count;
					Buffer.MemoryCopy(types, param_types_ptr, size, size);
				}
			}

			MethodKey mkey = new MethodKey(type.FullName!, name!, param_types, count);

			if (methods.TryGetValue(mkey, out minfo)) {
				if (minfo != null) {
					return minfo;
				}
				LogMessage($"Cached method '{type.FullName}.{name}[{count}]' was null!.", MessageLevel.Error);
			}
			
			List<MethodInfo> method_info_list = new List<MethodInfo>();
			method_info_list.AddRange(type.GetMethods(flags));
			
			Type? baseType = type.BaseType;
			while (baseType != null) {
				method_info_list.AddRange(baseType.GetMethods(flags));
				baseType = baseType.BaseType;
			}

			minfo = InteropInterface.FindSuitableMethod<MethodInfo>(name, types, count, CollectionsMarshal.AsSpan(method_info_list));
			if (minfo != null) {
				methods.Add(mkey, minfo!);
				return minfo;
			} else {
				LogMessage($"Method '{type.FullName}.{name}[{count}]' not found.", MessageLevel.Error);
				return null;
			}
		}

		[UnmanagedCallersOnly]
		private static unsafe IntPtr CreateObject(Int32 typeid, NBool32 weak_ref, IntPtr parameters, ManagedType* param_types, Int32 count) {
			try {
				if (!InteropInterface.cached_types.TryGet(typeid, out var type)) {
					LogMessage($"Type with ID '{typeid}' not found in cache.", MessageLevel.Error);
					return IntPtr.Zero;
				}
				if (type == null) {
					LogMessage($"Type with ID '{typeid}' is null.", MessageLevel.Error);
					return IntPtr.Zero;
				}

				ReadOnlySpan<ConstructorInfo> ctors = type.GetConstructors(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
				ConstructorInfo? ctor = InteropInterface.FindSuitableMethod(".ctor", param_types, count, ctors);
				if (ctor == null) {
					LogMessage($"No suitable constructor found for type '{type.FullName}'.", MessageLevel.Error);
					return IntPtr.Zero;
				}

				List<MethodInfo> method_info_list = new List<MethodInfo>();
				method_info_list.AddRange(type.GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance));
				StringBuilder sb = new StringBuilder();
				sb.Append($"Creating Object of Type '{type.FullName}'\n");
				sb.Append($"	> Methods for type '{type.FullName}':\n");
				for (Int32 i = 0; i < method_info_list.Count; i++) {
					sb.Append($"  ----  Method '{type.FullName}.{method_info_list[i].Name}[{method_info_list[i].GetParameters().Length}]'\n");
				}
				LogMessage(sb.ToString(), MessageLevel.Trace);

				/// this will be null of count == 0, which is fine (parameters will be null as well)
				object?[]? marshalled_parameters = Interop.DotOtherMarshal.MarshalParameterArray(parameters, count, ctor);
				object? result = null;

				if (marshalled_parameters == null) {
					result = InteropInterface.CreateInstance(type);
				} else {
					result = InteropInterface.CreateInstance(type, marshalled_parameters);
					ctor.Invoke(result, marshalled_parameters);
				}

				if (result == null) {
					LogMessage($"Failed to create instance of type '{type.FullName}'.", MessageLevel.Error);
					return IntPtr.Zero;
				}

				var handle = GCHandle.Alloc(result, weak_ref ? GCHandleType.Weak : GCHandleType.Normal);
				AssemblyLoader.RegisterHandle(type.Assembly, handle);
				return GCHandle.ToIntPtr(handle);
			} catch (Exception e) {
				HandleException(e);
				return IntPtr.Zero;
			}
		}

		[UnmanagedCallersOnly]
		private static unsafe void DestroyObject(IntPtr handle) {
			try {
				GCHandle.FromIntPtr(handle).Free();
			} catch (Exception e) {
				HandleException(e);
			}
		}

		[UnmanagedCallersOnly]
		private static unsafe void InvokeMethod(IntPtr handle, NString method_name, IntPtr parameters, ManagedType* param_types, int count) {
			try {
				// LogMessage($"Attempting to invoke method '{method_name}' on object with handle '{handle}'.", MessageLevel.Trace);
				if (method_name == null) {
					throw new ArgumentNullException($"{nameof(method_name)} cannot be null.");
				}

				object? target = GCHandle.FromIntPtr(handle).Target;
				if (target == null) {
					throw new NullReferenceException($"Target object for invoking method [{method_name}]({count}) is null.");
				}

				Type target_type = target.GetType();
				// LogMessage($"	> InvokeMethod target object found, type : [{target_type.FullName}]", MessageLevel.Trace);

				MethodInfo? minfo = TryGetMethodInfo(target_type, method_name, param_types, count, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
				if (minfo == null) {
					throw new MissingMethodException($"Method '{target_type.FullName}.{method_name}[{count}]' not found.");
				}
				// LogMessage($"	> Method info [{target_type.FullName}.{method_name}] found", MessageLevel.Trace);
					
				object?[]? marshalled_parameters = Interop.DotOtherMarshal.MarshalParameterArray(parameters, count, minfo);
				minfo.Invoke(target, marshalled_parameters);
			} catch (Exception ex) {
				LogMessage($"InvokeMethod({method_name}[{count}]) failed", MessageLevel.Error);
				HandleException(ex);
			}
		}

		[UnmanagedCallersOnly]
		private static unsafe void InvokeMethodRet(IntPtr handle , NString name , IntPtr parameters, ManagedType* param_types, Int32 count, IntPtr res) {
			try {
				var target = GCHandle.FromIntPtr(handle).Target;

				if (target == null) {
					LogMessage($"Cannot invoke method {name} on a null type.", MessageLevel.Error);
					return;
				}

				if (name == null) {
					LogMessage("Method name is null.", MessageLevel.Error);
					return;
				}

				var target_type = target.GetType();
				var method_info = TryGetMethodInfo(target_type, name, param_types, count, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
				if (method_info == null) {
					LogMessage($"Method  ['{target_type.Name}.{name}'] was not found", MessageLevel.Error);
					return;
				}

				var marshalled_parameters = Interop.DotOtherMarshal.MarshalParameterArray(parameters, count, method_info);
				object? value = method_info.Invoke(target, marshalled_parameters);
				if (value == null) {
					return;
				}

				Interop.DotOtherMarshal.MarshalReturn(value, method_info.ReturnType, res);
			} catch (Exception e) {
				HandleException(e);
			}
		}

		[UnmanagedCallersOnly]
		private static unsafe void InvokeStaticMethod(Int32 handle, NString name, IntPtr parameters, ManagedType* param_types, Int32 count) {
			try {
				if (!InteropInterface.cached_types.TryGet(handle, out var type)) {
					LogMessage($"Type with ID '{handle}' not found in cache.", MessageLevel.Error);
					return;
				}

				if (type == null) {
					LogMessage($"Type with ID '{handle}' is null.", MessageLevel.Error);
					return;
				}

				var method_info = TryGetMethodInfo(type, name, param_types, count, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
				if (method_info == null) {
					LogMessage($"Method  ['{type.Name}.{name}'] was not found", MessageLevel.Error);
					return;
				}

				var marshalled_parameters = Interop.DotOtherMarshal.MarshalParameterArray(parameters, count, method_info);
				method_info.Invoke(null, marshalled_parameters);
			} catch (Exception e) {
				HandleException(e);
			}
		}

		[UnmanagedCallersOnly]
		private static unsafe void InvokeStaticMethodRet(Int32 handle, NString name , IntPtr parameters, ManagedType* param_types, Int32 count, IntPtr res) {
			try {
				if (!InteropInterface.cached_types.TryGet(handle, out var type)) {
					LogMessage($"Type with ID '{handle}' not found in cache.", MessageLevel.Error);
					return;
				}

				if (type == null) {
					LogMessage($"Type with ID '{handle}' is null.", MessageLevel.Error);
					return;
				}

				var method_info = TryGetMethodInfo(type, name, param_types, count, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
				if (method_info == null) {
					LogMessage($"Method  ['{type.Name}.{name}'] was not found", MessageLevel.Error);
					return;
				}

				var marshalled_parameters = Interop.DotOtherMarshal.MarshalParameterArray(parameters, count, method_info);
				object? value = method_info.Invoke(null, marshalled_parameters);
				if (value == null) {
					return;
				}

				Interop.DotOtherMarshal.MarshalReturn(value, method_info.ReturnType, IntPtr.Zero);
			} catch (Exception e) {
				HandleException(e);
			}
		}

		[UnmanagedCallersOnly]
		private static unsafe void SetField(IntPtr target , NString name, IntPtr value) {
			try {
				var obj = GCHandle.FromIntPtr(target).Target;
				if (obj == null) {
					LogMessage("Target object is null.", MessageLevel.Error);
					return;
				}

				var type = obj.GetType();
				var field = type.GetField(name!, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
				if (field == null) {
					LogMessage($"Field '{name}' not found in type '{type.FullName}'.", MessageLevel.Error);
					return;
				}

				var marshalled_value = Interop.DotOtherMarshal.MarshalPointer(value , field.FieldType);
				field.SetValue(obj, marshalled_value);
			} catch (Exception e) {
				HandleException(e);
			}
		}

		[UnmanagedCallersOnly]
		private static unsafe void GetField(IntPtr target, NString name, IntPtr res) {
			try {
				var obj = GCHandle.FromIntPtr(target).Target;
				if (obj == null) {
					LogMessage("Target object is null.", MessageLevel.Error);
					return;
				}

				var type = obj.GetType();
				var field = type.GetField(name!, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
				if (field == null) {
					LogMessage($"Field '{name}' not found in type '{type.FullName}'.", MessageLevel.Error);
					return;
				}

				var value = field.GetValue(obj);
				Interop.DotOtherMarshal.MarshalReturn(value, field.FieldType, res);
			} catch (Exception e) {
				HandleException(e);
			}
		}

		[UnmanagedCallersOnly]
		private static unsafe void SetProperty(IntPtr target, NString name, IntPtr value) {
			try {
				var obj = GCHandle.FromIntPtr(target).Target;
				if (obj == null) {
					LogMessage("Target object is null.", MessageLevel.Error);
					return;
				}

				var type = obj.GetType();
				var prop = type.GetProperty(name!, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
				if (prop == null) {
					LogMessage($"Property '{name}' not found in type '{type.FullName}'.", MessageLevel.Error);
					return;
				}

				var marshalled_value = Interop.DotOtherMarshal.MarshalPointer(value, prop.PropertyType);
				prop.SetValue(obj, marshalled_value);
			} catch (Exception e) {
				HandleException(e);
			}
		}

		[UnmanagedCallersOnly]
		private static unsafe void GetProperty(IntPtr target, NString name, IntPtr res) {
			try {
				var obj = GCHandle.FromIntPtr(target).Target;
				if (obj == null) {
					LogMessage("Target object is null.", MessageLevel.Error);
					return;
				}

				var type = obj.GetType();
				var prop = type.GetProperty(name!, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
				if (prop == null) {
					LogMessage($"Property '{name}' not found in type '{type.FullName}'.", MessageLevel.Error);
					return;
				}

				var value = prop.GetValue(obj);
				Interop.DotOtherMarshal.MarshalReturn(value, prop.PropertyType, res);
			} catch (Exception e) {
				HandleException(e);
			}
		}
	}
#nullable disable

}