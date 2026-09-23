// Dumps the public and protected surface of one assembly, one line per member, with C# nullable
// reference annotations and System.Diagnostics.CodeAnalysis attributes rendered. Two dumps of the
// same assembly at different revisions diff line by line; see ../README.md.
//
//   dotnet run --project diagnostics/J15/PublicApi -- <assembly.dll> [output.txt]
using System.Reflection;
using System.Runtime.Loader;
using System.Text;

if (args.Length is < 1 or > 2)
{
    Console.Error.WriteLine("usage: PublicApi <assembly.dll> [output.txt]");
    return 2;
}

// A collectible context keeps the inspected copy apart from anything this tool itself loaded.
var context = new AssemblyLoadContext("inspected", isCollectible: true);
var directory = Path.GetDirectoryName(Path.GetFullPath(args[0]))!;
// A provider's signatures name engine types; resolve them from the inspected build's own output.
context.Resolving += (loader, name) =>
    Path.Combine(directory, name.Name + ".dll") is var path && File.Exists(path) ? loader.LoadFromAssemblyPath(path) : null;
var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(args[0]));
var nullability = new NullabilityInfoContext();
var lines = new List<string>();

foreach (var type in assembly.GetExportedTypes().OrderBy(t => t.FullName, StringComparer.Ordinal))
{
    lines.Add(TypeHeader(type));
    var members = new List<string>();
    const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                             BindingFlags.Static | BindingFlags.DeclaredOnly;

    if (type.IsEnum)
    {
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
            members.Add($"{field.Name} = {Convert.ToUInt64(field.GetRawConstantValue())}");
    }
    else if (typeof(Delegate).IsAssignableFrom(type))
    {
        members.Add("Invoke" + Method(type.GetMethod("Invoke")!));
    }
    else
    {
        foreach (var ctor in type.GetConstructors(all).Where(Visible))
            members.Add(Access(ctor) + "ctor" + Parameters(ctor.GetParameters()));
        foreach (var field in type.GetFields(all).Where(f => Visible(f)))
            members.Add($"{Access(field)}{(field.IsStatic ? "static " : "")}{(field.IsLiteral ? "const " : field.IsInitOnly ? "readonly " : "")}" +
                        $"{Render(field.FieldType, nullability.Create(field))} {field.Name}{Attributes(field)}");
        foreach (var property in type.GetProperties(all))
        {
            var getter = property.GetMethod is { } g && Visible(g) ? g : null;
            var setter = property.SetMethod is { } s && Visible(s) ? s : null;
            if (getter is null && setter is null)
                continue;
            var info = nullability.Create(property);
            var accessor = getter ?? setter!;
            var index = property.GetIndexParameters();
            var name = index.Length == 0 ? property.Name : "this" + Parameters(index, '[', ']');
            var parts = new List<string>();
            if (getter is not null)
                parts.Add("get");
            if (setter is not null)
                parts.Add(setter.ReturnParameter.GetRequiredCustomModifiers()
                    .Any(m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit") ? "init" : "set");
            var type0 = getter is not null
                ? Render(property.PropertyType, info, write: false)
                : Render(property.PropertyType, info, write: true);
            var writeNote = getter is not null && setter is not null && info.ReadState != info.WriteState
                ? $" (write: {info.WriteState})" : "";
            members.Add($"{Access(accessor)}{(accessor.IsStatic ? "static " : "")}{type0} {name} {{ {string.Join("; ", parts)}; }}" +
                        writeNote + Attributes(property));
        }
        foreach (var ev in type.GetEvents(all).Where(e => e.AddMethod is { } a && Visible(a)))
            members.Add($"{Access(ev.AddMethod!)}event {Render(ev.EventHandlerType!, nullability.Create(ev))} {ev.Name}");
        foreach (var method in type.GetMethods(all).Where(m => Visible(m) && (!m.IsSpecialName || m.Name.StartsWith("op_", StringComparison.Ordinal))))
            members.Add($"{Access(method)}{(method.IsStatic ? "static " : "")}{method.Name}{Method(method)}");
    }

    members.Sort(StringComparer.Ordinal);
    lines.AddRange(members.Select(m => "    " + m));
}

var text = string.Join("\n", lines) + "\n";
if (args.Length == 2)
    File.WriteAllText(args[1], text, new UTF8Encoding(false));
else
    Console.Write(text);
return 0;

static bool Visible(MemberInfo member) => member switch
{
    MethodBase m => m.IsPublic || m.IsFamily || m.IsFamilyOrAssembly,
    FieldInfo f => f.IsPublic || f.IsFamily || f.IsFamilyOrAssembly,
    _ => false,
};

static string Access(MemberInfo member) => member switch
{
    MethodBase { IsPublic: true } or FieldInfo { IsPublic: true } => "public ",
    _ => "protected ",
};

string TypeHeader(Type type)
{
    var kind = type.IsEnum ? "enum" : type.IsInterface ? "interface" : type.IsValueType ? "struct"
        : typeof(Delegate).IsAssignableFrom(type) ? "delegate" : "class";
    var modifiers = new StringBuilder();
    if (type.IsValueType && type.IsByRefLike)
        modifiers.Append("ref ");
    if (type.IsValueType && type.GetCustomAttributes().Any(a => a.GetType().Name == "IsReadOnlyAttribute"))
        modifiers.Insert(0, "readonly ");
    if (kind == "class" && type.IsAbstract && type.IsSealed)
        modifiers.Append("static ");
    else if (kind == "class" && type.IsSealed)
        modifiers.Append("sealed ");
    else if (kind == "class" && type.IsAbstract)
        modifiers.Append("abstract ");
    var bases = new List<string>();
    if (type.IsEnum)
        bases.Add(Enum.GetUnderlyingType(type).Name);
    else if (kind == "class" && type.BaseType is { } b && b != typeof(object))
        bases.Add(b.FullName!);
    if (kind is not ("enum" or "delegate"))
        bases.AddRange(type.GetInterfaces().Select(i => Render(i, null)).OrderBy(n => n, StringComparer.Ordinal));
    var flags = type.IsEnum && type.IsDefined(typeof(FlagsAttribute)) ? "[Flags] " : "";
    return $"{flags}{modifiers}{kind} {type.FullName}{(bases.Count == 0 ? "" : " : " + string.Join(", ", bases))}";
}

string Method(MethodInfo method)
{
    var generic = method.IsGenericMethodDefinition
        ? "<" + string.Join(", ", method.GetGenericArguments().Select(a => a.Name)) + ">" : "";
    var returns = Render(method.ReturnType, nullability.Create(method.ReturnParameter));
    return $"{generic}{Parameters(method.GetParameters())} : {returns}{Attributes(method.ReturnParameter, "return: ")}";
}

string Parameters(ParameterInfo[] parameters, char open = '(', char close = ')') =>
    open + string.Join(", ", parameters.Select(Parameter)) + close;

string Parameter(ParameterInfo parameter)
{
    var modifier = parameter.ParameterType.IsByRef
        ? parameter.IsOut ? "out " : parameter.IsIn ? "in " : "ref "
        : "";
    var rendered = Render(parameter.ParameterType, nullability.Create(parameter));
    var defaultValue = !parameter.HasDefaultValue ? ""
        : " = " + (parameter.RawDefaultValue ?? (parameter.ParameterType.IsValueType ? "default" : "null"));
    return $"{Attributes(parameter)}{modifier}{rendered} {parameter.Name}{defaultValue}";
}

static string Attributes(object provider, string target = "")
{
    var data = provider switch
    {
        ParameterInfo p => p.GetCustomAttributesData(),
        MemberInfo m => m.GetCustomAttributesData(),
        _ => [],
    };
    // Only the flow-analysis attributes change what a caller may assume; the compiler's own
    // Nullable/NullableContext metadata is already expressed through the rendered '?' marks.
    var names = data
        .Where(a => a.AttributeType.Namespace == "System.Diagnostics.CodeAnalysis")
        .Select(a => a.AttributeType.Name.Replace("Attribute", "") +
                     (a.ConstructorArguments.Count == 0 ? "" : "(" + string.Join(", ", a.ConstructorArguments.Select(c => c.Value)) + ")"))
        .OrderBy(n => n, StringComparer.Ordinal)
        .ToList();
    if (names.Count == 0)
        return "";
    var rendered = "[" + target + string.Join(", ", names) + "]";
    return target.Length == 0 && provider is ParameterInfo ? rendered + " " : " " + rendered;
}

static string Render(Type type, NullabilityInfo? info, bool write = false)
{
    if (type.IsByRef)
        return Render(type.GetElementType()!, info, write);
    if (Nullable.GetUnderlyingType(type) is { } underlying)
        return Render(underlying, null) + "?";
    string name;
    if (type.IsArray)
        name = Render(type.GetElementType()!, info?.ElementType) + "[]";
    else if (type.IsGenericType)
    {
        var definition = type.GetGenericTypeDefinition().FullName ?? type.Name;
        definition = definition[..definition.IndexOf('`')];
        var arguments = type.GetGenericArguments();
        name = definition + "<" + string.Join(", ", arguments.Select((a, i) =>
            Render(a, info is { GenericTypeArguments.Length: > 0 } ? info.GenericTypeArguments[i] : null))) + ">";
    }
    else
        name = type.IsGenericParameter ? type.Name : type.FullName ?? type.Name;
    var state = info is null ? NullabilityState.Unknown : write ? info.WriteState : info.ReadState;
    return name + (!type.IsValueType && state == NullabilityState.Nullable ? "?"
                   : !type.IsValueType && state == NullabilityState.Unknown && info is not null ? "~" : "");
}
