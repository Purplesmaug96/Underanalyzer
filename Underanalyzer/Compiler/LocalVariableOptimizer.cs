/*
  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at https://mozilla.org/MPL/2.0/.
*/

using System.Collections.Generic;
using Underanalyzer.Compiler.Parser;
using Underanalyzer.Compiler.Nodes;
using static Underanalyzer.IGMInstruction;

namespace Underanalyzer.Compiler;

/// <summary>
/// Performs reference counting of local variables across the AST, and uses those counts to
/// enable local variable optimizations during bytecode generation:
/// <list type="bullet">
/// <item>Locals that are written to but never read can have their stores eliminated
/// (while still evaluating the initializer for its side effects).</item>
/// <item>Locals that are written exactly once (via a constant initializer) and read exactly once
/// can be inlined, replacing the read with the constant value.</item>
/// </list>
/// </summary>
internal static class LocalVariableOptimizer
{
    /// <summary>
    /// Counts all reads and writes of local variables in the given root node, per function scope.
    /// </summary>
    public static void CountReferences(ParseContext context, IASTNode root)
    {
        CountNode(context, root, context.RootScope);
    }

    /// <summary>
    /// Marks eligible local variables for inlining. Must be called after <see cref="CountReferences"/>.
    /// </summary>
    public static void MarkInlinableLocals(ParseContext context, IASTNode root)
    {
        MarkInlinableInContext(context, root, context.RootScope);
    }

    /// <summary>
    /// Walks a node, counting reads and writes of local variables in the given function scope.
    /// </summary>
    private static void CountNode(ParseContext context, IASTNode node, FunctionScope scope)
    {
        // Function declarations introduce a new function scope for all of their children
        if (node is FunctionDeclNode funcDecl)
        {
            FunctionScope funcScope = funcDecl.Scope;
            if (funcDecl.DefaultValueBlock is not null)
            {
                CountNode(context, funcDecl.DefaultValueBlock, funcScope);
            }
            CountNode(context, funcDecl.Body, funcScope);
            if (funcDecl.InheritanceCall is not null)
            {
                CountNode(context, funcDecl.InheritanceCall, funcScope);
            }
            if (funcScope.StaticInitializerBlock is not null)
            {
                CountNode(context, funcScope.StaticInitializerBlock, funcScope);
            }
            return;
        }

        switch (node)
        {
            case AssignNode assign:
                CountAssignDestination(context, assign, scope);
                CountNode(context, assign.Expression, scope);
                break;

            case LocalVarDeclNode localDecl:
                // Initializer expressions are read normally
                foreach (IASTNode? expression in localDecl.AssignedValues)
                {
                    if (expression is not null)
                    {
                        CountNode(context, expression, scope);
                    }
                }
                // Each declaration writes to its local variable
                foreach (string name in localDecl.DeclaredLocals)
                {
                    scope.IncrementLocalWrite(name);
                }
                break;

            case PrefixNode prefix:
                CountReadWrite(context, prefix.Expression, scope);
                break;

            case PostfixNode postfix:
                CountReadWrite(context, postfix.Expression, scope);
                break;

            case NullishCoalesceAssignNode nullishCoalesceAssign:
                CountReadWrite(context, nullishCoalesceAssign.Destination, scope);
                CountNode(context, nullishCoalesceAssign.Expression, scope);
                break;

            default:
                // Regular read of a plain local variable
                if (node is SimpleVariableNode { ExplicitInstanceType: InstanceType.Local } simpleVar)
                {
                    scope.IncrementLocalRead(simpleVar.VariableName, simpleVar);
                }
                foreach (IASTNode child in node.EnumerateChildren())
                {
                    CountNode(context, child, scope);
                }
                break;
        }
    }

    /// <summary>
    /// Counts the destination side of an assignment. Plain local destinations are pure writes;
    /// anything else (accessors, dot chains) reads its base variables instead.
    /// </summary>
    private static void CountAssignDestination(ParseContext context, AssignNode assign, FunctionScope scope)
    {
        if (assign.Kind == AssignNode.AssignKind.Normal)
        {
            // A write to a plain local variable
            if (assign.Destination is SimpleVariableNode { ExplicitInstanceType: InstanceType.Local } destVar)
            {
                scope.IncrementLocalWrite(destVar.VariableName);
                return;
            }
        }
        else
        {
            // Compound assignments read and write the destination
            CountReadWrite(context, assign.Destination, scope);
            return;
        }

        // All other destinations still read their base variable(s)
        CountNode(context, assign.Destination, scope);
    }

    /// <summary>
    /// Counts a node that is both read and written (compound assignments, pre/post-increments).
    /// </summary>
    private static void CountReadWrite(ParseContext context, IASTNode node, FunctionScope scope)
    {
        if (node is SimpleVariableNode { ExplicitInstanceType: InstanceType.Local } localVar)
        {
            scope.IncrementLocalRead(localVar.VariableName, localVar);
            scope.IncrementLocalWrite(localVar.VariableName);
            return;
        }

        // All other destinations still read their base variable(s)
        CountNode(context, node, scope);
    }

    /// <summary>
    /// Walks a node, marking eligible local variables for inlining in the given function scope.
    /// </summary>
    private static void MarkInlinableInContext(ParseContext context, IASTNode node, FunctionScope scope)
    {
        // Function declarations introduce a new function scope for all of their children
        if (node is FunctionDeclNode funcDecl)
        {
            if (funcDecl.DefaultValueBlock is not null)
            {
                MarkInlinableInContext(context, funcDecl.DefaultValueBlock, funcDecl.Scope);
            }
            MarkInlinableInContext(context, funcDecl.Body, funcDecl.Scope);
            if (funcDecl.InheritanceCall is not null)
            {
                MarkInlinableInContext(context, funcDecl.InheritanceCall, funcDecl.Scope);
            }
            if (funcDecl.Scope.StaticInitializerBlock is not null)
            {
                MarkInlinableInContext(context, funcDecl.Scope.StaticInitializerBlock, funcDecl.Scope);
            }
            return;
        }

        // Only inline from statements that are in the same block as (and before) their read,
        // since that guarantees the write executes before the read.
        if (node is BlockNode block)
        {
            for (int i = 0; i < block.Children.Count; i++)
            {
                IASTNode child = block.Children[i];

                if (child is LocalVarDeclNode localDecl)
                {
                    TryInlineFromDeclaration(context, block, i, localDecl, scope);
                }

                MarkInlinableInContext(context, child, scope);
            }
            return;
        }

        foreach (IASTNode child in node.EnumerateChildren())
        {
            MarkInlinableInContext(context, child, scope);
        }
    }

    /// <summary>
    /// Attempts to mark locals declared by a local variable declaration statement as inlinable.
    /// </summary>
    private static void TryInlineFromDeclaration(ParseContext context, BlockNode block, int statementIndex, LocalVarDeclNode localDecl, FunctionScope scope)
    {
        for (int j = 0; j < localDecl.DeclaredLocals.Count; j++)
        {
            string name = localDecl.DeclaredLocals[j];
            IASTNode? value = localDecl.AssignedValues[j];

            // Only constant values can be safely inlined at the site of the read
            if (value is not IConstantASTNode)
            {
                continue;
            }

            // Must have exactly one read and exactly one write (which is this declaration)
            if (!scope.TryGetLocalReferences(name, out LocalVariableReferences? refs))
            {
                continue;
            }
            if (refs.Reads != 1 || refs.Writes != 1 || refs.SingleReadNode is not SimpleVariableNode readNode)
            {
                continue;
            }

            // The sole read must be in a later statement of this block, and never within a write target
            if (!IsReadWithinLaterStatements(block.Children, statementIndex + 1, readNode))
            {
                continue;
            }

            // Mark for inlining
            refs.InlineValue = value;
        }
    }

    /// <summary>
    /// Returns whether the given read node occurs somewhere within the given statements.
    /// </summary>
    private static bool IsReadWithinLaterStatements(List<IASTNode> statements, int startIndex, SimpleVariableNode target)
    {
        for (int i = startIndex; i < statements.Count; i++)
        {
            if (SubtreeContainsRead(statements[i], target, false))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Returns whether the given read node occurs within a subtree. When <paramref name="inWriteContext"/>
    /// is <see langword="true"/>, the node is inside a write target (e.g. the destination of an assignment),
    /// and reads there could be mutated, so they are not considered eligible.
    /// </summary>
    private static bool SubtreeContainsRead(IASTNode node, SimpleVariableNode target, bool inWriteContext)
    {
        if (ReferenceEquals(node, target))
        {
            return !inWriteContext;
        }

        switch (node)
        {
            // Function declarations have their own scope; the target (a local of another scope)
            // cannot be referenced within them.
            case FunctionDeclNode:
                return false;

            case AssignNode assign:
                return SubtreeContainsRead(assign.Expression, target, false) ||
                       SubtreeContainsRead(assign.Destination, target, true);

            case PrefixNode prefix:
                return SubtreeContainsRead(prefix.Expression, target, true);

            case PostfixNode postfix:
                return SubtreeContainsRead(postfix.Expression, target, true);

            case NullishCoalesceAssignNode nullishCoalesceAssign:
                return SubtreeContainsRead(nullishCoalesceAssign.Expression, target, false) ||
                       SubtreeContainsRead(nullishCoalesceAssign.Destination, target, true);

            default:
                foreach (IASTNode child in node.EnumerateChildren())
                {
                    if (SubtreeContainsRead(child, target, false))
                    {
                        return true;
                    }
                }
                return false;
        }
    }
}