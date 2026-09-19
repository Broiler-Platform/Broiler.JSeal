namespace Broiler.JSeal;

/// <summary>
/// The property attributes a member is installed with.
/// </summary>
/// <remarks>
/// <para>
/// When this enum was cut, the DOM bridge installed 1,246 members with exactly four combinations of
/// Broiler.JS's <c>JSPropertyAttributes</c>: <c>EnumerableConfigurableValue</c> (908),
/// <c>EnumerableConfigurableProperty</c> (331), <c>ConfigurableValue</c> (6) and
/// <c>ConfigurableProperty</c> (1). Nothing was installed read-only, non-configurable, or with an
/// explicit writable flag, and read-only was expressed by passing a null setter at 216 sites.
/// </para>
/// <para>
/// So this enum carries the three WebIDL-relevant bits and nothing else. The value/accessor
/// distinction is <b>not</b> a flag here â€” it is which method you call, <see cref="IJsMembers.DefineValue"/>
/// or <see cref="IJsMembers.DefineAccessor"/>. Encoding it as a flag is what let the two disagree in the
/// first place: a value installed with the accessor bit set, or the reverse, is a mistake the
/// compiler cannot catch, and an engine whose property storage separates the two (most do) has to
/// re-derive the answer the caller already knew.
/// </para>
/// </remarks>
[Flags]
public enum JsPropertyFlags : byte
{
    /// <summary>Not enumerable, not configurable, not writable.</summary>
    None = 0,

    /// <summary>Appears in <c>forâ€¦in</c> and <c>Object.keys</c>.</summary>
    Enumerable = 1,

    /// <summary>May be redefined or deleted.</summary>
    Configurable = 2,

    /// <summary>
    /// May be assigned to. Meaningful only for a value property; an accessor's writability is whether
    /// it has a setter.
    /// </summary>
    Writable = 4,

    /// <summary>
    /// What almost every DOM member is: enumerable, configurable, and â€” for a value property â€”
    /// writable. This is the WebIDL default for an operation or an attribute on an interface.
    /// </summary>
    Default = Enumerable | Configurable | Writable,

    /// <summary>
    /// Configurable but not enumerable â€” what <c>Storage</c>'s five methods, its <c>length</c>, and
    /// <c>PerformanceObserver.prototype</c> use, and the only other combination the bridge needs.
    /// </summary>
    NonEnumerable = Configurable | Writable,
}

