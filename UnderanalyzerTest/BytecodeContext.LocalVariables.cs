/*
  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at https://mozilla.org/MPL/2.0/.
*/

using Underanalyzer;
using Underanalyzer.Mock;

namespace UnderanalyzerTest;

public class BytecodeContext_LocalVariables
{
    private static GameContextMock SafeContext()
    {
        return new GameContextMock
        {
            OptimizationLevel = CompilerOptimizationLevel.Safe
        };
    }

    [Fact]
    public void TestLocalVarDeadStore_ConstantInitializer()
    {
        // GameMaker compiler level is unchanged
        TestUtil.AssertBytecode(
            """
            var x = 5;
            """,
            """
            pushi.e 5
            pop.v.i local.x
            """
        );

        // Safe level eliminates the store, keeping the value evaluation
        TestUtil.AssertBytecode(
            """
            var x = 5;
            """,
            """
            pushi.e 5
            popz.i
            """,
            gameContext: SafeContext()
        );
    }

    [Fact]
    public void TestLocalVarDeadStore_ExpressionInitializer()
    {
        // GameMaker compiler level is unchanged
        TestUtil.AssertBytecode(
            """
            var x = test_builtin_function();
            """,
            """
            call.i test_builtin_function 0
            pop.v.v local.x
            """
        );

        // Safe level eliminates the store, but still evaluates the call for its side effects
        TestUtil.AssertBytecode(
            """
            var x = test_builtin_function();
            """,
            """
            call.i test_builtin_function 0
            popz.v
            """,
            gameContext: SafeContext()
        );
    }

    [Fact]
    public void TestLocalVarInline()
    {
        // GameMaker compiler level is unchanged
        TestUtil.AssertBytecode(
            """
            var x = 5;
            real(x);
            """,
            """
            pushi.e 5
            pop.v.i local.x
            pushloc.v local.x
            call.i real 1
            popz.v
            """
        );

        // Safe level inlines the constant into the single read
        TestUtil.AssertBytecode(
            """
            var x = 5;
            real(x);
            """,
            """
            pushi.e 5
            conv.i.v
            call.i real 1
            popz.v
            """,
            gameContext: SafeContext()
        );
    }

    [Fact]
    public void TestLocalVarNoInline_MultipleReads()
    {
        // A local read more than once cannot be inlined, and must not be eliminated
        TestUtil.AssertBytecode(
            """
            var x = 5;
            real(x);
            real(x);
            """,
            """
            pushi.e 5
            pop.v.i local.x
            pushloc.v local.x
            call.i real 1
            popz.v
            pushloc.v local.x
            call.i real 1
            popz.v
            """,
            gameContext: SafeContext()
        );
    }

    [Fact]
    public void TestLocalVarNoInline_MultipleWrites()
    {
        // A local written more than once cannot be inlined, and must not be eliminated
        TestUtil.AssertBytecode(
            """
            var x = 5;
            x = 6;
            real(x);
            """,
            """
            pushi.e 5
            pop.v.i local.x
            pushi.e 6
            pop.v.i local.x
            pushloc.v local.x
            call.i real 1
            popz.v
            """,
            gameContext: SafeContext()
        );
    }

    [Fact]
    public void TestLocalVarInline_WithinExpression()
    {
        // GameMaker compiler level is unchanged
        TestUtil.AssertBytecode(
            """
            var x = 5;
            real(x + 1);
            """,
            """
            pushi.e 5
            pop.v.i local.x
            pushloc.v local.x
            pushi.e 1
            add.i.v
            call.i real 1
            popz.v
            """
        );

        // Safe level replaces the read with the constant within the expression
        TestUtil.AssertBytecode(
            """
            var x = 5;
            real(x + 1);
            """,
            """
            pushi.e 5
            pushi.e 1
            add.i.i
            conv.i.v
            call.i real 1
            popz.v
            """,
            gameContext: SafeContext()
        );
    }
}