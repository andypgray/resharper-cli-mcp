using System.Diagnostics.CodeAnalysis;
using Shouldly;

namespace Zphil.ReSharperCli.Tests;

/// <summary>
///     Makes the suite's substring assertions compare case, shadowing the Shouldly originals that default to
///     <see cref="Case.Insensitive" />.
/// </summary>
/// <remarks>
///     <para>
///         The strings this suite asserts on are pinned product text: error messages, log lines, rendered markdown,
///         tool and environment variable names. Their casing is part of what is pinned, and an insensitive default
///         passes a change that alters only capitalisation. These overloads take fewer parameters than
///         Shouldly's, so a call that names no <see cref="Case" /> binds here. A call site where case is not
///         content passes <c>Case.Insensitive</c> with its reason beside it; no overload here takes a
///         <see cref="Case" />, so that call falls through to Shouldly. Each name has a twin taking
///         <c>customMessage</c>, under Shouldly's own parameter name, because without one a call that passes a
///         message would bind to Shouldly's insensitive overload.
///     </para>
///     <para>
///         The namespace is what makes this work. Extension lookup walks outward from the innermost enclosing
///         namespace and stops at the first one holding an applicable method, so a class in the suite's root
///         namespace is reached before the compilation unit's <c>using Shouldly</c>, from every test file in every
///         nested namespace. <c>CaseSensitiveStringAssertionsTests</c> pins that from a nested namespace.
///     </para>
///     <para>
///         <c>ShouldNotContain</c>, <c>ShouldNotStartWith</c> and <c>ShouldNotEndWith</c> are left to Shouldly's
///         insensitive default, because an absence check that ignores case is the stronger claim.
///     </para>
///     <para>
///         The inner calls are qualified static calls rather than <c>actual.ShouldContain(…)</c>, so an overload
///         added here can never capture them; the ReSharper disables stop cleanup from rewriting them into the
///         extension form. <see cref="ShouldlyMethodsAttribute" /> is on the class so a failure quotes the
///         caller's line rather than this file's.
///     </para>
/// </remarks>
[ShouldlyMethods]
internal static class CaseSensitiveStringAssertions
{
    // ReSharper disable once InvokeAsExtensionMethod
    // ReSharper disable once InvokeAsExtensionMember
    public static void ShouldContain(this string actual, string expected)
    {
        ShouldBeStringTestExtensions.ShouldContain(actual, expected, Case.Sensitive);
    }

    // ReSharper disable once InvokeAsExtensionMethod
    // ReSharper disable once InvokeAsExtensionMember
    public static void ShouldContain(this string actual, string expected, string? customMessage)
    {
        ShouldBeStringTestExtensions.ShouldContain(actual, expected, Case.Sensitive, customMessage);
    }

    // ReSharper disable once InvokeAsExtensionMethod
    // ReSharper disable once InvokeAsExtensionMember
    public static void ShouldStartWith([NotNull] this string? actual, string expected)
    {
        ShouldBeStringTestExtensions.ShouldStartWith(actual, expected, Case.Sensitive);
    }

    // ReSharper disable once InvokeAsExtensionMethod
    // ReSharper disable once InvokeAsExtensionMember
    public static void ShouldStartWith([NotNull] this string? actual, string expected, string? customMessage)
    {
        ShouldBeStringTestExtensions.ShouldStartWith(actual, expected, Case.Sensitive, customMessage);
    }

    // ReSharper disable once InvokeAsExtensionMethod
    // ReSharper disable once InvokeAsExtensionMember
    public static void ShouldEndWith([NotNull] this string? actual, string expected)
    {
        ShouldBeStringTestExtensions.ShouldEndWith(actual, expected, Case.Sensitive);
    }

    // ReSharper disable once InvokeAsExtensionMethod
    // ReSharper disable once InvokeAsExtensionMember
    public static void ShouldEndWith([NotNull] this string? actual, string expected, string? customMessage)
    {
        ShouldBeStringTestExtensions.ShouldEndWith(actual, expected, Case.Sensitive, customMessage);
    }
}