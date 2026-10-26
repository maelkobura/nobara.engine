// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Nobara.Analyzers.Tests;

public sealed class AsyncAnalyzerTests
{
    private static void Verify(string markup) =>
        AnalyzerVerifier.Verify(new AsyncAnalyzer(), "using System; using System.Threading.Tasks; using Nobara.Core;\n" + markup);

    [Fact]
    public void Accepts_async_outside_loop_code()
    {
        Verify("""
            class Loader
            {
                async Task LoadAsync() => await Task.Delay(1);
                [LoopCallback] void Update() { }
                [HotPath] Task Pending() => Task.CompletedTask;
            }
            """);
    }

    [Fact]
    public void Reports_async_methods_marked_as_loop_callbacks_or_hot_paths()
    {
        Verify("""
            class Mesher
            {
                [LoopCallback] {|NOB301:async|} Task Update() => await Task.Yield();
                [HotPath] {|NOB301:async|} ValueTask Build() => await Task.Yield();
            }
            """);
    }

    [Fact]
    public void Reports_async_lambdas_and_local_functions_inside_loop_code()
    {
        Verify("""
            class Mesher
            {
                [LoopCallback]
                void Update()
                {
                    Func<Task> a = {|NOB301:async|} () => await Task.Yield();
                    {|NOB301:async|} Task Local() => await Task.Yield();
                }
            }
            """);
    }

    [Fact]
    public void Covers_implementations_of_marked_interface_methods_and_marked_types()
    {
        Verify("""
            interface IRenderStep { [LoopCallback] Task Render(); }
            class Sky : IRenderStep { public {|NOB301:async|} Task Render() => await Task.Yield(); }
            [LoopCallback] class Hud { public {|NOB301:async|} Task Draw() => await Task.Yield(); }
            """);
    }

    [Fact]
    public void Names_the_construct_and_the_marking()
    {
        var diagnostic = Assert.Single(AnalyzerVerifier.Run(new AsyncAnalyzer(), """
            using System.Threading.Tasks; using Nobara.Core;
            class Mesher { [LoopCallback] async Task Update() => await Task.Yield(); }
            """));

        Assert.Equal("Method 'Update' is async, which is not allowed in a loop callback", diagnostic.GetMessage());
    }
}
