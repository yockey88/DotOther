using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

using DotOther.Managed.Interop;

namespace DotOther.Managed {

  using static DotOtherHost;

  public enum AsmLoadStatus {
    Success ,
    NotFound , 
    Failed ,
    InvalidPath ,
    InvalidAssembly ,
    CorruptContext,
    UnknownError
  }

  public static class AssemblyLoader {
    private static readonly Dictionary<Type, AsmLoadStatus> load_errors = new();
    private static readonly Dictionary<Int32, AssemblyLoadContext> contexts = new();
    private static readonly Dictionary<Int32, Assembly> assemblies = new();
    private static Dictionary<Int32, List<GCHandle>> handles = new();

    private static AsmLoadStatus last_load_status = AsmLoadStatus.Success;
#nullable enable
    private static readonly AssemblyLoadContext? dotother_asm_context = AssemblyLoadContext.Default;
#nullable disable

    static AssemblyLoader() {
      load_errors.Add(typeof(BadImageFormatException), AsmLoadStatus.InvalidAssembly);
      load_errors.Add(typeof(FileNotFoundException), AsmLoadStatus.NotFound);
      load_errors.Add(typeof(FileLoadException), AsmLoadStatus.Failed);
      load_errors.Add(typeof(ArgumentNullException), AsmLoadStatus.InvalidPath);
      load_errors.Add(typeof(ArgumentException), AsmLoadStatus.InvalidAssembly);
      load_errors.Add(typeof(NullReferenceException), AsmLoadStatus.CorruptContext);

      dotother_asm_context = AssemblyLoadContext.GetLoadContext(typeof(AssemblyLoader).Assembly);
      dotother_asm_context!.Resolving += ResolveAssembly;
      CacheDotOtherAssemblies();
    }

    private static void CacheDotOtherAssemblies() {
      foreach (var assembly in dotother_asm_context!.Assemblies)  {
        int assemblyId = assembly.GetName().Name!.GetHashCode();
        assemblies.Add(assemblyId, assembly);
      }
    }

#nullable enable
    internal static bool TryGetAssembly(Int32 id , out Assembly? asm) {
      return assemblies.TryGetValue(id, out asm);
    }

    internal static Assembly? ResolveAssembly(AssemblyLoadContext? context, AssemblyName asm_name) {
      try {
        Int32 asm_id = asm_name.Name!.GetHashCode();
        if (assemblies.TryGetValue(asm_id, out var asm)) {
          return asm;
        }

        foreach (var alc in contexts.Values) {
          foreach (var assembly  in alc.Assemblies) {
            if (assembly.GetName().Name != asm_name.Name) {
              continue;
            }

            assemblies.Add(asm_id, assembly);
            return assembly;
          }
        }
      } catch (Exception e) {
        LogMessage($"Failed to resolve assembly {asm_name} | \n\t{e.StackTrace}", MessageLevel.Error);
        HandleException(e);
      }
      return null;
    }

    [UnmanagedCallersOnly]
    private static Int32 CreateAssemblyLoadContext(NString context_name) {
      string? name = context_name;

      if (name == null) {
        return -1;
      }

      var alc = new AssemblyLoadContext(name, true);

      alc.Resolving += ResolveAssembly;
      alc.Unloading += ctx => {
        foreach (var asm in ctx.Assemblies) {
          var asm_name = asm.GetName();
          Int32 asm_id = asm_name.Name!.GetHashCode();
          assemblies.Remove(asm_id);
        }
      };

      Int32 ctx_id = name.GetHashCode();
      contexts.Add(ctx_id, alc);

      return ctx_id;
    }

    
	  [UnmanagedCallersOnly]
	  private static void UnloadAssemblyLoadContext(Int32 context_id) {
      if (!contexts.TryGetValue(context_id, out var alc)) {
        LogMessage($"Cannot unload AssemblyLoadContext '{context_id}', it was either never loaded or already unloaded.", MessageLevel.Warning);
        return;
      }

      if (alc == null) {
        LogMessage($"AssemblyLoadContext '{context_id}' was found in dictionary but was null. This is most likely a bug.", MessageLevel.Error);
        return;
      }

      foreach (var assembly in alc.Assemblies) {
        var asm_name = assembly.GetName();
        int asm_id = asm_name.Name!.GetHashCode();

        if (!handles.TryGetValue(asm_id, out var hs)) {
          continue;
        }

        foreach (var h in hs) {
          if (!h.IsAllocated || h.Target == null) {
            continue;
          }
          h.Free();
        }
      }

      InteropInterface.cached_types.Clear();
      InteropInterface.cached_methods.Clear();
      InteropInterface.cached_fields.Clear();
      InteropInterface.cached_properties.Clear();
      InteropInterface.cached_attributes.Clear();

      contexts.Remove(context_id);
      alc.Unload();
    }

    static Assembly? dotother_assembly = null;

    private enum CoreAssembly {
      SystemPrivateCoreLib,
      // SystemRuntime,
      // SystemConsole,
      // SystemLinq,
      // SystemCollections,
      // SystemNetHttp,
      // SystemIO,
      // SystemThreading,
      NumCoreAssemblies
    }

    
    private static string[] core_assembly_names = new string[] {
        "System.Private.CoreLib",
        // "System.Runtime",
        // "System.Console",
        // "System.Linq",
        // "System.Collections",
        // "System.Net.Http",
        // "System.IO",
        // "System.Threading"
    };

    private static Assembly[] core_assemblies = new Assembly[(int)CoreAssembly.NumCoreAssemblies];

    private static List<Type> core_types = new List<Type>();

    public static ReadOnlySpan<Type> CoreTypes {
      get => core_types.ToArray();
    }

    private static bool core_assemblies_loaded = false;

    public static bool CoreAsmsLoaded {
      get => core_assemblies_loaded;
    }

    private static Assembly? LoadNetCoreAssembly(CoreAssembly assembly_type, string assembly_name) {
      try {
        Assembly? assembly = null;
        if (core_assemblies[(int)assembly_type] == null) {
          assembly = Assembly.Load(assembly_name);
          if (assembly == null) {
            LogMessage($"Failed to load core assembly '{assembly_name}', assembly is null", MessageLevel.Error);
            return null;
          }

          ReadOnlySpan<Type> types = assembly.GetTypes();
          core_assemblies[(int)assembly_type] = assembly;

          LogMessage($"Loaded core assembly : {core_assemblies[(int)assembly_type].FullName}", MessageLevel.Trace);
          LogMessage($" > Number of types in core assembly : {types.Length}", MessageLevel.Trace);
          foreach (var type in types) {
            core_types.Add(type);
          }
        } else {
          LogMessage($"Core assembly already loaded : {core_assemblies[(int)assembly_type].FullName}", MessageLevel.Trace);
          assembly = core_assemblies[(int)assembly_type];
        }

        return assembly;
      } catch (Exception e) {
        LogMessage($"Failed to load core assembly '{assembly_name}' | \n\t{e.StackTrace}", MessageLevel.Error);
        return null;
      }
    }


    public static Type? CheckNetCoreType(NString name) {
      Type? type = null;

      for (int i = 0; i < (int)CoreAssembly.NumCoreAssemblies; i++) {
        if (core_assemblies[i] == null) {
          continue;
        }

        type = core_assemblies[i].GetType(name!);
        if (type != null) {
          break;
        }
      }

      return type;
    }

    public static void LoadNetCoreAssemblies() {      
      LogMessage("Loading .NET Core assemblies", MessageLevel.Info);
      for (int i = 0; i < (int)CoreAssembly.NumCoreAssemblies; i++) {
        Assembly? core_asm = LoadNetCoreAssembly((CoreAssembly)i, core_assembly_names[i]);
        if (core_asm == null) {
          break;
        }

        core_assemblies[i] = core_asm;
      }

      core_assemblies_loaded = true;

      try {
        if (dotother_assembly == null) {
          dotother_assembly = Assembly.GetExecutingAssembly();
          if (dotother_assembly == null) {
            LogMessage($"Failed to load system assembly, assembly is null", MessageLevel.Error);
            return;
          }
          
          ReadOnlySpan<Type> types = dotother_assembly.GetTypes();
          foreach (var type in types) {
            LogMessage($"> System Type : {type.FullName}", MessageLevel.Trace);
          }
        }
      } catch (Exception e) {
        LogMessage($"Failed to load system system assembly | \n\t{e.StackTrace}", MessageLevel.Error);
      }
    }
    
    [UnmanagedCallersOnly]
    private static int LoadAssembly(int context_id, NString file_path) {
      try {
        LogMessage($"Loading assembly '{file_path}' [{context_id}]", MessageLevel.Trace);

        if (string.IsNullOrEmpty(file_path)) {
          last_load_status = AsmLoadStatus.InvalidPath;
          LogMessage($"Failed to load assembly : '{file_path}', path is invalid", MessageLevel.Error);
          return -1;
        }

        if (!File.Exists(file_path)) {
          last_load_status = AsmLoadStatus.NotFound;
          LogMessage($"Failed to load assembly : '{file_path}', file not found", MessageLevel.Error);
          return -1;
        } else {
          LogMessage($" > Found assembly file '{file_path}'", MessageLevel.Trace);
        }

        if (!contexts.TryGetValue(context_id, out var alc)) {
          last_load_status = AsmLoadStatus.InvalidAssembly;
          LogMessage($"Failed to load assembly '{file_path}', couldn't find Load Context with id '{context_id}'", MessageLevel.Error);
          return -1;
        } else {
          LogMessage($" > Found Load Context with id '{context_id}'", MessageLevel.Trace);
        }

        if (alc == null) {
          last_load_status = AsmLoadStatus.CorruptContext;
          LogMessage($"Failed to load assembly '{file_path}', Load Context with id '{context_id}' is null", MessageLevel.Error);
          return -1;
        } else {
          LogMessage($" > Load Context with id '{context_id}' is not null", MessageLevel.Trace);
        }

        Assembly? asm = null;

        using (var file = MemoryMappedFile.CreateFromFile(file_path!)) {
          using var stream = file.CreateViewStream();
          asm = alc.LoadFromStream(stream);
        }

        var name = asm.GetName();
        LogMessage($"Successfully loaded assembly : '{name}' [{context_id}]", MessageLevel.Info);
        
        Int32 asm_id = name.Name!.GetHashCode();
        try {
          assemblies.Add(asm_id, asm);
        } catch (Exception e) {
          last_load_status = AsmLoadStatus.Failed;
          HandleException(e);
          return -1;
        }

        last_load_status = AsmLoadStatus.Success;
        return asm_id;
      } catch (Exception e) {
        LogMessage($"Failed to load assembly '{file_path}' | \n\t{e.StackTrace}", MessageLevel.Error);
        HandleException(e);
        return -1;
      }
    }

    [UnmanagedCallersOnly]
    private static AsmLoadStatus GetLastLoadStatus() => last_load_status;

    [UnmanagedCallersOnly]
    private static NString GetAsmName(Int32 asm_id) {
      if (!assemblies.TryGetValue(asm_id, out var asm)) {
        LogMessage($"Couldn't get assembly name for assembly '{asm_id}', assembly not found!", MessageLevel.Error);
        return "<unknown>";
      }

      var asm_name = asm.GetName();
      return asm_name.Name;
    }

    internal static void RegisterHandle(Assembly asm , GCHandle handle) {
      var asm_name = asm.GetName();
      Int32 asm_id = asm_name.Name!.GetHashCode();

      if (!handles.TryGetValue(asm_id , out var hs)) {
        handles.Add(asm_id, new List<GCHandle>());
        hs = handles[asm_id];
      }

      hs.Add(handle);
    }
#nullable disable
  }

}