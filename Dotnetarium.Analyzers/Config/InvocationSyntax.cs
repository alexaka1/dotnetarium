using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Dotnetarium.Config
{
    /// <summary>Cheap candidate selection before semantic validation of framework calls.</summary>
    internal static class InvocationSyntax
    {
        internal static string? Name(ExpressionSyntax expression)
        {
            while (expression is ParenthesizedExpressionSyntax parenthesized) expression = parenthesized.Expression;
            return expression switch
            {
                MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
                MemberBindingExpressionSyntax member => member.Name.Identifier.ValueText,
                SimpleNameSyntax name => name.Identifier.ValueText,
                _ => null
            };
        }
    }
}
