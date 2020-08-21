﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using OmniSharp.Models.v1.Completion;
using OmniSharp.Roslyn.CSharp.Services.Completion;
using TestUtility;
using Xunit;
using Xunit.Abstractions;

namespace OmniSharp.Roslyn.CSharp.Tests
{
    public class CompletionFacts : AbstractTestFixture
    {
        private const int ImportCompletionTimeout = 1000;
        private readonly ILogger _logger;

        private string EndpointName => OmniSharpEndpoints.Completion;

        public CompletionFacts(ITestOutputHelper output, SharedOmniSharpHostFixture sharedOmniSharpHostFixture)
            : base(output, sharedOmniSharpHostFixture)
        {
            this._logger = this.LoggerFactory.CreateLogger<CompletionFacts>();
        }

        [Theory]
        [InlineData("dummy.cs")]
        [InlineData("dummy.csx")]
        public async Task PropertyCompletion(string filename)
        {
            const string input =
                @"public class Class1 {
                    public int Foo { get; set; }
                    public Class1()
                        {
                            Foo$$
                        }
                    }";

            var completions = await FindCompletionsAsync(filename, input, SharedOmniSharpTestHost);
            Assert.Contains("Foo", completions.Items.Select(c => c.Label));
            Assert.Contains("Foo", completions.Items.Select(c => c.InsertText));
        }

        [Theory]
        [InlineData("dummy.cs")]
        [InlineData("dummy.csx")]
        public async Task VariableCompletion(string filename)
        {
            const string input =
                @"public class Class1 {
                    public Class1()
                        {
                            var foo = 1;
                            foo$$
                        }
                    }";

            var completions = await FindCompletionsAsync(filename, input, SharedOmniSharpTestHost);
            Assert.Contains("foo", completions.Items.Select(c => c.Label));
            Assert.Contains("foo", completions.Items.Select(c => c.InsertText));
        }

        [Theory]
        [InlineData("dummy.cs")]
        [InlineData("dummy.csx")]
        public async Task DocumentationIsResolved(string filename)
        {
            const string input =
                @"public class Class1 {
                    public Class1()
                        {
                            Foo$$
                        }
                    /// <summary>Some Text</summary>
                    public void Foo(int bar = 1)
                        {
                        }
                    }";

            var completions = await FindCompletionsAsync(filename, input, SharedOmniSharpTestHost);
            Assert.All(completions.Items, c => Assert.Null(c.Documentation));

            var fooCompletion = completions.Items.Single(c => c.Label == "Foo");
            var resolvedCompletion = await ResolveCompletionAsync(fooCompletion, SharedOmniSharpTestHost);
            Assert.Equal("```csharp\nvoid Class1.Foo([int bar = 1])\n```\n\nSome Text", resolvedCompletion.Item.Documentation);
        }

        [Theory]
        [InlineData("dummy.cs")]
        [InlineData("dummy.csx")]
        public async Task ReturnsCamelCasedCompletions(string filename)
        {
            const string input =
                @"public class Class1 {
                    public Class1()
                        {
                            System.Guid.tp$$
                        }
                    }";

            var completions = await FindCompletionsAsync(filename, input, SharedOmniSharpTestHost);
            Assert.Contains("TryParse", completions.Items.Select(c => c.InsertText));
        }

        [Theory]
        [InlineData("dummy.cs")]
        [InlineData("dummy.csx")]
        public async Task ImportCompletionTurnedOff(string filename)
        {
            const string input =
@"public class Class1 {
    public Class1()
    {
        Gui$$
    }
}";

            var completions = await FindCompletionsAsync(filename, input, SharedOmniSharpTestHost);
            Assert.False(completions.IsIncomplete);
            Assert.DoesNotContain("Guid", completions.Items.Select(c => c.InsertText));
        }

        [Theory]
        [InlineData("dummy.cs")]
        [InlineData("dummy.csx")]
        public async Task ImportCompletionResolvesOnSubsequentQueries(string filename)
        {
            const string input =
@"public class Class1 {
    public Class1()
    {
        Gui$$
    }
}";

            using var host = GetImportCompletionHost();

            // First completion request should kick off the task to update the completion cache.
            var completions = await FindCompletionsAsync(filename, input, host);
            Assert.True(completions.IsIncomplete);
            Assert.DoesNotContain("Guid", completions.Items.Select(c => c.InsertText));

            // Populating the completion cache should take no more than a few ms, don't let it take too
            // long
            CancellationTokenSource cts = new CancellationTokenSource(millisecondsDelay: ImportCompletionTimeout);
            await Task.Run(async () =>
            {
                while (completions.IsIncomplete)
                {
                    completions = await FindCompletionsAsync(filename, input, host);
                    cts.Token.ThrowIfCancellationRequested();
                }
            }, cts.Token);

            Assert.False(completions.IsIncomplete);
            Assert.Contains("Guid", completions.Items.Select(c => c.InsertText));
        }

        [Theory]
        [InlineData("dummy.cs")]
        [InlineData("dummy.csx")]
        public async Task ImportCompletion_LocalsPrioritizedOverImports(string filename)
        {

            const string input =
@"public class Class1 {
    public Class1()
    {
        string guid;
        Gui$$
    }
}";

            using var host = GetImportCompletionHost();
            var completions = await FindCompletionsWithImportedAsync(filename, input, host);
            CompletionItem localCompletion = completions.Items.First(c => c.InsertText == "guid");
            CompletionItem typeCompletion = completions.Items.First(c => c.InsertText == "Guid");
            Assert.True(localCompletion.Data < typeCompletion.Data);
            Assert.StartsWith("0", localCompletion.SortText);
            Assert.StartsWith("1", typeCompletion.SortText);
            VerifySortOrders(completions.Items);
        }

        [Theory]
        [InlineData("dummy.cs")]
        [InlineData("dummy.csx")]
        public async Task ImportCompletions_IncludesExtensionMethods(string filename)
        {
            const string input =
@"namespace N1
{
    public class C1
    {
        public void M(object o)
        {
            o.$$
        }
    }
}
namespace N2
{
    public static class ObjectExtensions
    {
        public static void Test(this object o)
        {
        }
    }
}";

            using var host = GetImportCompletionHost();
            var completions = await FindCompletionsWithImportedAsync(filename, input, host);
            Assert.Contains("Test", completions.Items.Select(c => c.InsertText));
            VerifySortOrders(completions.Items);
        }

        [Theory]
        [InlineData("dummy.cs")]
        [InlineData("dummy.csx")]
        public async Task ImportCompletion_ResolveAddsImportEdit(string filename)
        {
            const string input =
@"namespace N1
{
    public class C1
    {
        public void M(object o)
        {
            o.$$
        }
    }
}
namespace N2
{
    public static class ObjectExtensions
    {
        public static void Test(this object o)
        {
        }
    }
}";

            using var host = GetImportCompletionHost();
            var completions = await FindCompletionsWithImportedAsync(filename, input, host);
            var resolved = await ResolveCompletionAsync(completions.Items.First(c => c.InsertText == "Test"), host);

            Assert.Single(resolved.Item.AdditionalTextEdits);
            var additionalEdit = resolved.Item.AdditionalTextEdits[0];
            Assert.Equal(NormalizeNewlines("using N2;\n\nnamespace N1\r\n{\r\n    public class C1\r\n    {\r\n        public void M(object o)\r\n        {\r\n            o"),
                         additionalEdit.NewText);
            Assert.Equal(0, additionalEdit.StartLine);
            Assert.Equal(0, additionalEdit.StartColumn);
            Assert.Equal(6, additionalEdit.EndLine);
            Assert.Equal(13, additionalEdit.EndColumn);
            VerifySortOrders(completions.Items);
        }

        [Theory]
        [InlineData("dummy.cs")]
        [InlineData("dummy.csx")]
        public async Task SelectsLastInstanceOfCompletion(string filename)
        {
            const string input =
@"namespace N1
{
    public class C1
    {
        public void M(object o)
        {
            /*Guid*/$$//Guid
        }
    }
}
namespace N2
{
    public static class ObjectExtensions
    {
        public static void Test(this object o)
        {
        }
    }
}";

            using var host = GetImportCompletionHost();
            var completions = await FindCompletionsWithImportedAsync(filename, input, host);
            var resolved = await ResolveCompletionAsync(completions.Items.First(c => c.InsertText == "Guid"), host);

            Assert.Single(resolved.Item.AdditionalTextEdits);
            var additionalEdit = resolved.Item.AdditionalTextEdits[0];
            Assert.Equal(NormalizeNewlines("using System;\n\nnamespace N1\r\n{\r\n    public class C1\r\n    {\r\n        public void M(object o)\r\n        {\r\n            /*Guid*"),
                         additionalEdit.NewText);
            Assert.Equal(0, additionalEdit.StartLine);
            Assert.Equal(0, additionalEdit.StartColumn);
            Assert.Equal(6, additionalEdit.EndLine);
            Assert.Equal(19, additionalEdit.EndColumn);
            VerifySortOrders(completions.Items);
        }

        [Theory]
        [InlineData("dummy.cs")]
        [InlineData("dummy.csx")]
        public async Task UsingsAddedInOrder(string filename)
        {

            const string input =
@"using N1;
using N3;
namespace N1
{
    public class C1
    {
        public void M(object o)
        {
            $$
        }
    }
}
namespace N2
{
    public class C2
    {
    }
}
namespace N3
{
    public class C3
    {
    }
}";

            using var host = GetImportCompletionHost();
            var completions = await FindCompletionsWithImportedAsync(filename, input, host);
            var resolved = await ResolveCompletionAsync(completions.Items.First(c => c.InsertText == "C2"), host);

            Assert.Single(resolved.Item.AdditionalTextEdits);
            var additionalEdit = resolved.Item.AdditionalTextEdits[0];
            Assert.Equal(NormalizeNewlines("N2;\nusing N3;\r\nnamespace N1\r\n{\r\n    public class C1\r\n    {\r\n        public void M(object o)\r\n        {\r\n           "),
                         additionalEdit.NewText);
            Assert.Equal(1, additionalEdit.StartLine);
            Assert.Equal(6, additionalEdit.StartColumn);
            Assert.Equal(8, additionalEdit.EndLine);
            Assert.Equal(11, additionalEdit.EndColumn);
            VerifySortOrders(completions.Items);
        }

        [Theory]
        [InlineData("dummy.cs")]
        [InlineData("dummy.csx")]
        public async Task ReturnsSubsequences(string filename)
        {
            const string input =
                @"public class Class1 {
                    public Class1()
                        {
                            System.Guid.ng$$
                        }
                    }";

            var completions = await FindCompletionsAsync(filename, input, SharedOmniSharpTestHost);
            Assert.Contains("NewGuid", completions.Items.Select(c => c.Label));
        }

        [Theory]
        [InlineData("dummy.cs")]
        [InlineData("dummy.csx")]
        public async Task ReturnsSubsequencesWithoutFirstLetter(string filename)
        {
            const string input =
                @"public class Class1 {
                    public Class1()
                        {
                            System.Guid.gu$$
                        }
                    }";

            var completions = await FindCompletionsAsync(filename, input, SharedOmniSharpTestHost);
            Assert.Contains("NewGuid", completions.Items.Select(c => c.Label));
        }

        [Theory]
        [InlineData("dummy.cs")]
        [InlineData("dummy.csx")]
        public async Task MethodHeaderDocumentation(string filename)
        {
            const string input =
                @"public class Class1 {
                    public Class1()
                        {
                            System.Guid.ng$$
                        }
                    }";

            var completions = await FindCompletionsAsync(filename, input, SharedOmniSharpTestHost);
            Assert.All(completions.Items, c => Assert.Null(c.Documentation));

            var fooCompletion = completions.Items.Single(c => c.Label == "NewGuid");
            var resolvedCompletion = await ResolveCompletionAsync(fooCompletion, SharedOmniSharpTestHost);
            Assert.Equal("```csharp\nSystem.Guid System.Guid.NewGuid()\n```", resolvedCompletion.Item.Documentation);
        }

        [Theory]
        [InlineData("dummy.cs")]
        [InlineData("dummy.csx")]
        public async Task PreselectsCorrectCasing_Lowercase(string filename)
        {
            const string input =
                @"public class MyClass1 {

                    public MyClass1()
                        {
                            var myvar = 1;
                            my$$
                        }
                    }";

            var completions = await FindCompletionsAsync(filename, input, SharedOmniSharpTestHost);
            Assert.Contains(completions.Items, c => c.Label == "myvar");
            Assert.Contains(completions.Items, c => c.Label == "MyClass1");
            Assert.All(completions.Items, c =>
            {
                switch (c.Label)
                {
                    case "myvar":
                        Assert.True(c.Preselect);
                        break;
                    default:
                        Assert.False(c.Preselect);
                        break;
                }
            });
        }

        [Theory]
        [InlineData("dummy.cs")]
        [InlineData("dummy.csx")]
        public async Task PreselectsCorrectCasing_Uppercase(string filename)
        {
            const string input =
                @"public class MyClass1 {

                    public MyClass1()
                        {
                            var myvar = 1;
                            My$$
                        }
                    }";

            var completions = await FindCompletionsAsync(filename, input, SharedOmniSharpTestHost);
            Assert.Contains(completions.Items, c => c.Label == "myvar");
            Assert.Contains(completions.Items, c => c.Label == "MyClass1");
            Assert.All(completions.Items, c =>
            {
                switch (c.Label)
                {
                    case "MyClass1":
                        Assert.True(c.Preselect);
                        break;
                    default:
                        Assert.False(c.Preselect);
                        break;
                }
            });
        }

        [Theory]
        [InlineData("dummy.cs")]
        [InlineData("dummy.csx")]
        public async Task NoCompletionsInInvalid(string filename)
        {
            const string source =
                @"public class MyClass1 {

                    public MyClass1()
                        {
                            var x$$
                        }
                    }";

            var completions = await FindCompletionsAsync(filename, source, SharedOmniSharpTestHost);
            Assert.Empty(completions.Items);
        }

        [Theory]
        [InlineData("dummy.cs")]
        [InlineData("dummy.csx")]
        public async Task AttributeDoesNotHaveAttributeSuffix(string filename)
        {
            const string source =
                @"using System;

                    public class BarAttribute : Attribute {}

                    [B$$
                    public class Foo {}";

            var completions = await FindCompletionsAsync(filename, source, SharedOmniSharpTestHost);
            Assert.Contains(completions.Items, c => c.Label == "Bar");
            Assert.Contains(completions.Items, c => c.InsertText == "Bar");
            Assert.All(completions.Items, c =>
            {
                switch (c.Label)
                {
                    case "Bar":
                        Assert.True(c.Preselect);
                        break;
                    default:
                        Assert.False(c.Preselect);
                        break;
                }
            });
        }

        [Theory]
        [InlineData("dummy.cs")]
        [InlineData("dummy.csx")]
        public async Task ReturnsObjectInitalizerMembers(string filename)
        {
            const string source =
                @"public class MyClass1 {
                        public string Foo {get; set;}
                  }

                    public class MyClass2 {

                        public MyClass2()
                        {
                            var c = new MyClass1 {
                             F$$
                        }
                    }
                ";

            var completions = await FindCompletionsAsync(filename, source, SharedOmniSharpTestHost);
            Assert.Single(completions.Items);
            Assert.Equal("Foo", completions.Items[0].Label);
            Assert.Equal("Foo", completions.Items[0].InsertText);
        }

        [Theory]
        [InlineData("dummy.cs")]
        [InlineData("dummy.csx")]
        public async Task IncludesParameterNames(string filename)
        {
            const string source =
                @"public class MyClass1 {
                        public void SayHi(string text) {}
                  }

                    public class MyClass2 {

                        public MyClass2()
                        {
                            var c = new MyClass1();
                            c.SayHi(te$$
                        }
                    }
                ";

            var completions = await FindCompletionsAsync(filename, source, SharedOmniSharpTestHost);
            var item = completions.Items.First(c => c.Label == "text:");
            Assert.NotNull(item);
            Assert.Equal("text", item.InsertText);
            Assert.All(completions.Items, c =>
            {
                switch (c.Label)
                {
                    case "text:":
                        Assert.True(c.Preselect);
                        break;
                    default:
                        Assert.False(c.Preselect);
                        break;
                }
            });
        }

        [Theory]
        [InlineData("dummy.cs")]
        [InlineData("dummy.csx")]
        public async Task ReturnsNameSuggestions(string filename)
        {
            const string source =
                @"
public class MyClass
{
    MyClass m$$
}
                ";

            var completions = await FindCompletionsAsync(filename, source, SharedOmniSharpTestHost);
            Assert.Equal(new[] { "myClass", "my", "@class", "MyClass", "My", "Class", "GetMyClass", "GetMy", "GetClass" },
                         completions.Items.Select(c => c.Label));
        }

        [Theory]
        [InlineData("dummy.cs")]
        [InlineData("dummy.csx")]
        public async Task OverrideSignatures_Publics(string filename)
        {
            const string source = @"
class Foo
{
    public virtual void Test(string text) {}
    public virtual void Test(string text, string moreText) {}
}

class FooChild : Foo
{
    override $$
}
";

            var completions = await FindCompletionsAsync(filename, source, SharedOmniSharpTestHost);
            Assert.Equal(new[] { "Equals(object obj)", "GetHashCode()", "Test(string text)", "Test(string text, string moreText)", "ToString()" },
                         completions.Items.Select(c => c.Label));
            Assert.Equal(new[] { "Equals(object obj)\n    {\n        return base.Equals(obj);$0\n    \\}",
                                 "GetHashCode()\n    {\n        return base.GetHashCode();$0\n    \\}",
                                 "Test(string text)\n    {\n        base.Test(text);$0\n    \\}",
                                 "Test(string text, string moreText)\n    {\n        base.Test(text, moreText);$0\n    \\}",
                                 "ToString()\n    {\n        return base.ToString();$0\n    \\}"
                                },
                         completions.Items.Select<CompletionItem, string>(c => c.InsertText));

            Assert.Equal(new[] { "public override bool",
                                 "public override int",
                                 "public override void",
                                 "public override void",
                                 "public override string"},
                        completions.Items.Select(c => c.AdditionalTextEdits.Single().NewText));

            Assert.All(completions.Items.Select(c => c.AdditionalTextEdits.Single()),
                       r =>
                       {
                           Assert.Equal(9, r.StartLine);
                           Assert.Equal(4, r.StartColumn);
                           Assert.Equal(9, r.EndLine);
                           Assert.Equal(12, r.EndColumn);
                       });

            Assert.All(completions.Items, c => Assert.Equal(InsertTextFormat.Snippet, c.InsertTextFormat));
        }

        [Theory]
        [InlineData("dummy.cs")]
        [InlineData("dummy.csx")]
        public async Task OverrideSignatures_UnimportedTypesFullyQualified(string filename)
        {
            const string source = @"
using N2;
namespace N1
{
    public class CN1 {}
}
namespace N2
{
    using N1;
    public abstract class IN2 { protected abstract CN1 GetN1(); }
}
namespace N3
{
    class CN3 : IN2
    {
        override $$
    }
}";

            var completions = await FindCompletionsAsync(filename, source, SharedOmniSharpTestHost);
            Assert.Equal(new[] { "Equals(object obj)", "GetHashCode()", "GetN1()", "ToString()" },
                         completions.Items.Select(c => c.Label));

            Assert.Equal(new[] { "Equals(object obj)\n        {\n            return base.Equals(obj);$0\n        \\}",
                                 "GetHashCode()\n        {\n            return base.GetHashCode();$0\n        \\}",
                                 "GetN1()\n        {\n            throw new System.NotImplementedException();$0\n        \\}",
                                 "ToString()\n        {\n            return base.ToString();$0\n        \\}"
                               },
                         completions.Items.Select(c => c.InsertText));

            Assert.Equal(new[] { "public override bool",
                                 "public override int",
                                 "protected override N1.CN1",
                                 "public override string"},
                        completions.Items.Select(c => c.AdditionalTextEdits.Single().NewText));

            Assert.All(completions.Items.Select(c => c.AdditionalTextEdits.Single()),
                       r =>
                       {
                           Assert.Equal(15, r.StartLine);
                           Assert.Equal(8, r.StartColumn);
                           Assert.Equal(15, r.EndLine);
                           Assert.Equal(16, r.EndColumn);
                       });

            Assert.All(completions.Items, c => Assert.Equal(InsertTextFormat.Snippet, c.InsertTextFormat));
        }

        [Theory]
        [InlineData("dummy.cs")]
        [InlineData("dummy.csx")]
        public async Task OverrideSignatures_ModifierInFront(string filename)
        {
            const string source = @"
class C
{
    public override $$
}";

            var completions = await FindCompletionsAsync(filename, source, SharedOmniSharpTestHost);
            Assert.Equal(new[] { "Equals(object obj)", "GetHashCode()", "ToString()" },
                         completions.Items.Select(c => c.Label));

            Assert.Equal(new[] { "bool Equals(object obj)\n    {\n        return base.Equals(obj);$0\n    \\}",
                                 "int GetHashCode()\n    {\n        return base.GetHashCode();$0\n    \\}",
                                 "string ToString()\n    {\n        return base.ToString();$0\n    \\}"
                               },
                         completions.Items.Select(c => c.InsertText));

            Assert.All(completions.Items.Select(c => c.AdditionalTextEdits), a => Assert.Null(a));
            Assert.All(completions.Items, c => Assert.Equal(InsertTextFormat.Snippet, c.InsertTextFormat));
        }

        [Theory]
        [InlineData("dummy.cs")]
        [InlineData("dummy.csx")]
        public async Task OverrideSignatures_ModifierAndReturnTypeInFront(string filename)
        {
            const string source = @"
class C
{
    public override bool $$
}";

            var completions = await FindCompletionsAsync(filename, source, SharedOmniSharpTestHost);
            Assert.Equal(new[] { "Equals(object obj)" },
                         completions.Items.Select(c => c.Label));

            Assert.Equal(new[] { "Equals(object obj)\n    {\n        return base.Equals(obj);$0\n    \\}" },
                         completions.Items.Select(c => c.InsertText));

            Assert.All(completions.Items.Select(c => c.AdditionalTextEdits), a => Assert.Null(a));
            Assert.All(completions.Items, c => Assert.Equal(InsertTextFormat.Snippet, c.InsertTextFormat));
        }

        [Theory]
        [InlineData("dummy.cs")]
        [InlineData("dummy.csx")]
        public async Task OverrideSignatures_TestTest(string filename)
        {
            const string source = @"
class Test {}
abstract class Base
{
    protected abstract Test Test();
}
class Derived : Base
{
    override $$
}";

            var completions = await FindCompletionsAsync(filename, source, SharedOmniSharpTestHost);
            Assert.Equal(new[] { "Equals(object obj)", "GetHashCode()", "Test()", "ToString()" },
                         completions.Items.Select(c => c.Label));

            Assert.Equal(new[] { "Equals(object obj)\n    {\n        return base.Equals(obj);$0\n    \\}",
                                 "GetHashCode()\n    {\n        return base.GetHashCode();$0\n    \\}",
                                 "Test()\n    {\n        throw new System.NotImplementedException();$0\n    \\}",
                                 "ToString()\n    {\n        return base.ToString();$0\n    \\}"
                               },
                         completions.Items.Select(c => c.InsertText));

            Assert.Equal(new[] { "public override bool",
                                 "public override int",
                                 "protected override Test",
                                 "public override string"},
                        completions.Items.Select(c => c.AdditionalTextEdits.Single().NewText));

            Assert.All(completions.Items.Select(c => c.AdditionalTextEdits.Single()),
                       r =>
                       {
                           Assert.Equal(8, r.StartLine);
                           Assert.Equal(4, r.StartColumn);
                           Assert.Equal(8, r.EndLine);
                           Assert.Equal(12, r.EndColumn);
                       });

            Assert.All(completions.Items, c => Assert.Equal(InsertTextFormat.Snippet, c.InsertTextFormat));
        }

        [Fact]
        public async Task OverrideCompletion_TypesNeedImport()
        {
            const string baseText = @"
using System;
public class Base
{
    public virtual Action GetAction(Action a) => null;
}
";

            const string derivedText = @"
public class Derived : Base
{
    override $$
}";

            var completions = await FindCompletionsAsync("derived.cs", derivedText, SharedOmniSharpTestHost, additionalFiles: new[] { new TestFile("base.cs", baseText) });
            var item = completions.Items.Single(c => c.Label.StartsWith("GetAction"));
            Assert.Equal("GetAction(System.Action a)", item.Label);

            Assert.Single(item.AdditionalTextEdits);
            Assert.Equal(NormalizeNewlines("using System;\n\npublic class Derived : Base\r\n{\r\n    public override Action"), item.AdditionalTextEdits[0].NewText);
            Assert.Equal(1, item.AdditionalTextEdits[0].StartLine);
            Assert.Equal(0, item.AdditionalTextEdits[0].StartColumn);
            Assert.Equal(3, item.AdditionalTextEdits[0].EndLine);
            Assert.Equal(12, item.AdditionalTextEdits[0].EndColumn);
            Assert.Equal("", item.InsertText);
        }

        [Fact]
        public async Task OverrideCompletion_FromNullableToNonNullableContext()
        {
            const string text = @"
#nullable enable
public class Base
{
    public virtual object? M1(object? param) => throw null;
}
#nullable disable
public class Derived : Base
{
    override $$
}";

            var completions = await FindCompletionsAsync("derived.cs", text, SharedOmniSharpTestHost);
            var item = completions.Items.Single(c => c.Label.StartsWith("M1"));
            Assert.Equal("M1(object? param)", item.Label);

            Assert.Single(item.AdditionalTextEdits);
            Assert.Equal("public override object", item.AdditionalTextEdits[0].NewText);
            Assert.Equal(9, item.AdditionalTextEdits[0].StartLine);
            Assert.Equal(4, item.AdditionalTextEdits[0].StartColumn);
            Assert.Equal(9, item.AdditionalTextEdits[0].EndLine);
            Assert.Equal(12, item.AdditionalTextEdits[0].EndColumn);
            Assert.Equal("M1(object param)\n    {\n        return base;$0\n    \\}", item.InsertText);
        }

        [Theory]
        [InlineData("dummy.cs")]
        [InlineData("dummy.csx")]
        public async Task PartialCompletion(string filename)
        {
            const string source = @"
partial class C
{
    partial void M1(string param);
}
partial class C
{
    partial $$
}
";

            var completions = await FindCompletionsAsync(filename, source, SharedOmniSharpTestHost);
            Assert.Equal(new[] { "M1(string param)" },
                         completions.Items.Select(c => c.Label));

            Assert.Equal(new[] { "void M1(string param)\n    {\n        throw new System.NotImplementedException();$0\n    \\}" },
                         completions.Items.Select(c => c.InsertText));

            Assert.All(completions.Items.Select(c => c.AdditionalTextEdits), a => Assert.Null(a));
            Assert.All(completions.Items, c => Assert.Equal(InsertTextFormat.Snippet, c.InsertTextFormat));
        }

        [Fact]
        public async Task PartialCompletion_TypesNeedImport()
        {
            const string file1 = @"
using System;
public partial class C
{
    partial void M(Action a);
}
";

            const string file2 = @"
public partial class C
{
    partial $$
}";

            var completions = await FindCompletionsAsync("derived.cs", file2, SharedOmniSharpTestHost, additionalFiles: new[] { new TestFile("base.cs", file1) });
            var item = completions.Items.Single(c => c.Label.StartsWith("M"));

            Assert.Single(item.AdditionalTextEdits);
            Assert.Equal(NormalizeNewlines("using System;\n\npublic partial class C\r\n{\r\n    partial void"), item.AdditionalTextEdits[0].NewText);
            Assert.Equal(1, item.AdditionalTextEdits[0].StartLine);
            Assert.Equal(0, item.AdditionalTextEdits[0].StartColumn);
            Assert.Equal(3, item.AdditionalTextEdits[0].EndLine);
            Assert.Equal(11, item.AdditionalTextEdits[0].EndColumn);
            Assert.Equal("void M1(string param)\n    {\n        throw new System.NotImplementedException();$0\n    \\}", item.InsertText);
        }

        [Fact]
        public async Task PartialCompletion_FromNullableToNonNullableContext()
        {
            const string text = @"
#nullable enable
public partial class C
{
    partial void M1(object? param);
}
#nullable disable
public partial class C
{
    partial $$
}";

            var completions = await FindCompletionsAsync("derived.cs", text, SharedOmniSharpTestHost);
            var item = completions.Items.Single(c => c.Label.StartsWith("M1"));
            Assert.Equal("M1(object param)", item.Label);
            Assert.Null(item.AdditionalTextEdits);
            Assert.Equal("void M1(object param)\n    {\n        throw new System.NotImplementedException();$0\n    \\}", item.InsertText);
        }

        [Theory]
        [InlineData("dummy.cs")]
        [InlineData("dummy.csx")]
        public async Task OverrideSignatures_PartiallyTypedIdentifier(string filename)
        {
            const string source = @"
class C
{
    override Ge$$
}";

            var completions = await FindCompletionsAsync(filename, source, SharedOmniSharpTestHost);
            Assert.Equal(new[] { "Equals(object obj)", "GetHashCode()", "ToString()" },
                         completions.Items.Select(c => c.Label));

            Assert.Equal(new[] { "Equals(object obj)\n    {\n        return base.Equals(obj);$0\n    \\}",
                                 "GetHashCode()\n    {\n        return base.GetHashCode();$0\n    \\}",
                                 "ToString()\n    {\n        return base.ToString();$0\n    \\}"
                               },
                         completions.Items.Select(c => c.InsertText));

            Assert.Equal(new[] { "public override bool",
                                 "public override int",
                                 "public override string"},
                        completions.Items.Select(c => c.AdditionalTextEdits.Single().NewText));

            Assert.All(completions.Items.Select(c => c.AdditionalTextEdits.Single()),
                       r =>
                       {
                           Assert.Equal(3, r.StartLine);
                           Assert.Equal(4, r.StartColumn);
                           Assert.Equal(3, r.EndLine);
                           Assert.Equal(12, r.EndColumn);
                       });

            Assert.All(completions.Items, c =>
            {
                switch (c.Label)
                {
                    case "GetHashCode()":
                        Assert.True(c.Preselect);
                        break;
                    default:
                        Assert.False(c.Preselect);
                        break;
                }
            });

            Assert.All(completions.Items, c => Assert.Equal(InsertTextFormat.Snippet, c.InsertTextFormat));
        }

        [Theory]
        [InlineData("dummy.cs")]
        [InlineData("dummy.csx")]
        public async Task CrefCompletion(string filename)
        {
            const string source =
                @"  /// <summary>
                    /// A comment. <see cref=""My$$"" /> for more details
                    /// </summary>
                  public class MyClass1 {
                  }
                ";

            var completions = await FindCompletionsAsync(filename, source, SharedOmniSharpTestHost);
            Assert.Contains(completions.Items, c => c.Label == "MyClass1");
            Assert.All(completions.Items, c =>
            {
                switch (c.Label)
                {
                    case "MyClass1":
                        Assert.True(c.Preselect);
                        break;
                    default:
                        Assert.False(c.Preselect);
                        break;
                }
            });
        }

        [Theory]
        [InlineData("dummy.cs")]
        [InlineData("dummy.csx")]
        public async Task DocCommentTagCompletions(string filename)
        {
            const string source =
                @"  /// <summary>
                    /// A comment. <$$
                    /// </summary>
                  public class MyClass1 {
                  }
                ";

            var completions = await FindCompletionsAsync(filename, source, SharedOmniSharpTestHost);
            Assert.Equal(new[] { "!--$0-->",
                                 "![CDATA[$0]]>",
                                 "c",
                                 "code",
                                 "inheritdoc$0/>",
                                 "list type=\"$0\"",
                                 "para",
                                 "see cref=\"$0\"/>",
                                 "seealso cref=\"$0\"/>"
                         },
                         completions.Items.Select(c => c.InsertText));
            Assert.All(completions.Items, c => Assert.Equal(c.InsertText.Contains("$0"), c.InsertTextFormat == InsertTextFormat.Snippet));
        }

        [Fact]
        public async Task HostObjectCompletionInScripts()
        {
            const string source =
                "Prin$$";

            var completions = await FindCompletionsAsync("dummy.csx", source, SharedOmniSharpTestHost);
            Assert.Contains(completions.Items, c => c.Label == "Print");
            Assert.Contains(completions.Items, c => c.Label == "PrintOptions");
        }

        [Theory]
        [InlineData("dummy.cs")]
        [InlineData("dummy.csx")]
        public async Task NoCommitOnSpaceInLambdaParameter_MethodArgument(string filename)
        {
            const string source = @"
using System;
class C
{
    int CallMe(int i) => 42;

    void M(Func<int, int> a) { }
    void M(string unrelated) { }

    void M()
    {
        M(c$$
    }
}
";

            var completions = await FindCompletionsAsync(filename, source, SharedOmniSharpTestHost);

            Assert.True(completions.Items.All(c => c.IsSuggestionMode()));
        }

        [Theory]
        [InlineData("dummy.cs")]
        [InlineData("dummy.csx")]
        public async Task NoCommitOnSpaceInLambdaParameter_Initializer(string filename)
        {
            const string source = @"
using System;
class C
{
    int CallMe(int i) => 42;

    void M()
    {
        Func<int, int> a = c$$
    }
}
";

            var completions = await FindCompletionsAsync(filename, source, SharedOmniSharpTestHost);

            Assert.True(completions.Items.All(c => c.IsSuggestionMode()));
        }

        [Theory]
        [InlineData("dummy.cs")]
        [InlineData("dummy.csx")]
        public async Task CommitOnSpaceWithoutLambda_InArgument(string filename)
        {
            const string source = @"
using System;
class C
{
    int CallMe(int i) => 42;

    void M(int a) { }

    void M()
    {
        M(c$$
    }
}
";

            var completions = await FindCompletionsAsync(filename, source, SharedOmniSharpTestHost);

            Assert.True(completions.Items.All(c => !c.IsSuggestionMode()));
        }

        [Theory]
        [InlineData("dummy.cs")]
        [InlineData("dummy.csx")]
        public async Task CommitOnSpaceWithoutLambda_InInitializer(string filename)
        {
            const string source = @"
using System;
class C
{
    int CallMe(int i) => 42;

    void M()
    {
        int a = c$$
    }
}
";

            var completions = await FindCompletionsAsync(filename, source, SharedOmniSharpTestHost);

            Assert.True(completions.Items.All(c => !c.IsSuggestionMode()));
        }

        [Fact]
        public async Task ScriptingIncludes7_1()
        {
            const string source =
                @"
                  var number1 = 1;
                  var number2 = 2;
                  var tuple = (number1, number2);
                  tuple.n$$
                ";

            var completions = await FindCompletionsAsync("dummy.csx", source, SharedOmniSharpTestHost);
            Assert.Contains(completions.Items, c => c.Label == "number1");
            Assert.Contains(completions.Items, c => c.Label == "number2");
            Assert.All(completions.Items, c =>
            {
                switch (c.Label)
                {
                    case "number1":
                    case "number2":
                        Assert.True(c.Preselect);
                        break;
                    default:
                        Assert.False(c.Preselect);
                        break;
                }
            });
        }

        [Fact]
        public async Task ScriptingIncludes7_2()
        {
            const string source =
                @"
                  public class Foo { private protected int myValue = 0; }
                  public class Bar : Foo
                  {
                    public Bar()
                    {
                        var x = myv$$
                    }
                  }
                ";

            var completions = await FindCompletionsAsync("dummy.csx", source, SharedOmniSharpTestHost);
            Assert.Contains(completions.Items, c => c.Label == "myValue");
            Assert.All(completions.Items, c =>
            {
                switch (c.Label)
                {
                    case "myValue":
                        Assert.True(c.Preselect);
                        break;
                    default:
                        Assert.False(c.Preselect);
                        break;
                }
            });
        }

        [Fact]
        public async Task ScriptingIncludes8_0()
        {
            const string source =
                @"
                  class Point {
                    public Point(int x, int y) {
                      PositionX = x;
                      PositionY = y;
                    }
                    public int PositionX { get; }
                    public int PositionY { get; }
                  }
                  Point[] points = { new (1, 2), new (3, 4) };
                  points[0].Po$$
                ";

            var completions = await FindCompletionsAsync("dummy.csx", source, SharedOmniSharpTestHost);
            Assert.Contains(completions.Items, c => c.Label == "PositionX");
            Assert.Contains(completions.Items, c => c.Label == "PositionY");
            Assert.All(completions.Items, c =>
            {
                switch (c.Label)
                {
                    case "PositionX":
                    case "PositionY":
                        Assert.True(c.Preselect);
                        break;
                    default:
                        Assert.False(c.Preselect);
                        break;
                }
            });
        }

        [Theory]
        [InlineData("dummy.cs")]
        [InlineData("dummy.csx")]
        public async Task TriggeredOnSpaceForObjectCreation(string filename)
        {
            const string input =
@"public class Class1 {
    public M()
    {
        Class1 c = new $$
    }
}";

            var completions = await FindCompletionsAsync(filename, input, SharedOmniSharpTestHost, triggerChar: ' ');
            Assert.NotEmpty(completions.Items);
        }

        [Theory]
        [InlineData("dummy.cs")]
        [InlineData("dummy.csx")]
        public async Task ReturnsAtleastOnePreselectOnNew(string filename)
        {
            const string input =
@"public class Class1 {
    public M()
    {
        Class1 c = new $$
    }
}";

            var completions = await FindCompletionsAsync(filename, input, SharedOmniSharpTestHost, triggerChar: ' ');
            Assert.NotEmpty(completions.Items.Where(completion => completion.Preselect == true));
        }

        [Theory]
        [InlineData("dummy.cs")]
        [InlineData("dummy.csx")]
        public async Task NotTriggeredOnSpaceWithoutObjectCreation(string filename)
        {
            const string input =
@"public class Class1 {
    public M()
    {
        $$
    }
}";

            var completions = await FindCompletionsAsync(filename, input, SharedOmniSharpTestHost, triggerChar: ' ');
            Assert.Empty(completions.Items);
        }

        [Fact]
        public async Task InternalsVisibleToCompletion()
        {
            var projectInfo = ProjectInfo.Create(
                ProjectId.CreateNewId(),
                VersionStamp.Create(),
                "ProjectNameVal",
                "AssemblyNameVal",
                LanguageNames.CSharp,
                "/path/to/project.csproj");

            SharedOmniSharpTestHost.Workspace.AddProject(projectInfo);

            const string input = @"
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(""$$";

            var completions = await FindCompletionsAsync("dummy.cs", input, SharedOmniSharpTestHost);
            Assert.Single(completions.Items);
            Assert.Equal("AssemblyNameVal", completions.Items[0].Label);
            Assert.Equal("AssemblyNameVal", completions.Items[0].InsertText);
        }

        [Fact]
        public async Task InternalsVisibleToCompletionSkipsMiscProject()
        {
            var projectInfo = ProjectInfo.Create(
                ProjectId.CreateNewId(),
                VersionStamp.Create(),
                "ProjectNameVal",
                "AssemblyNameVal",
                LanguageNames.CSharp,
                "/path/to/project.csproj");

            SharedOmniSharpTestHost.Workspace.AddProject(projectInfo);

            var miscFile = "class Foo {}";
            var miscFileLoader = TextLoader.From(TextAndVersion.Create(SourceText.From(miscFile), VersionStamp.Create()));
            SharedOmniSharpTestHost.Workspace.TryAddMiscellaneousDocument("dummy.cs", miscFileLoader, LanguageNames.CSharp);

            const string input = @"
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(""$$";

            var completions = await FindCompletionsAsync("dummy.cs", input, SharedOmniSharpTestHost);
            Assert.Single(completions.Items);
            Assert.Equal("AssemblyNameVal", completions.Items[0].Label);
            Assert.Equal("AssemblyNameVal", completions.Items[0].InsertText);
        }

        private CompletionService GetCompletionService(OmniSharpTestHost host)
            => host.GetRequestHandler<CompletionService>(EndpointName);

        protected async Task<CompletionResponse> FindCompletionsAsync(string filename, string source, OmniSharpTestHost testHost, char? triggerChar = null, TestFile[] additionalFiles = null)
        {
            var testFile = new TestFile(filename, source);

            var files = new[] { testFile };
            if (additionalFiles is object)
            {
                files = files.Concat(additionalFiles).ToArray();
            }

            testHost.AddFilesToWorkspace(files);
            var point = testFile.Content.GetPointFromPosition();

            var request = new CompletionRequest
            {
                Line = point.Line,
                Column = point.Offset,
                FileName = testFile.FileName,
                Buffer = testFile.Content.Code,
                CompletionTrigger = triggerChar is object ? CompletionTriggerKind.TriggerCharacter : CompletionTriggerKind.Invoked,
                TriggerCharacter = triggerChar
            };

            var requestHandler = GetCompletionService(testHost);

            return await requestHandler.Handle(request);
        }

        private async Task<CompletionResponse> FindCompletionsWithImportedAsync(string filename, string source, OmniSharpTestHost host)
        {
            var completions = await FindCompletionsAsync(filename, source, host);
            if (!completions.IsIncomplete)
            {
                return completions;
            }

            // Populating the completion list should take no more than a few ms, don't let it take too
            // long
            CancellationTokenSource cts = new CancellationTokenSource(millisecondsDelay: ImportCompletionTimeout);
            await Task.Run(async () =>
            {
                while (completions.IsIncomplete)
                {
                    completions = await FindCompletionsAsync(filename, source, host);
                    cts.Token.ThrowIfCancellationRequested();
                }
            }, cts.Token);

            Assert.False(completions.IsIncomplete);
            return completions;
        }

        protected async Task<CompletionResolveResponse> ResolveCompletionAsync(CompletionItem completionItem, OmniSharpTestHost testHost)
            => await GetCompletionService(testHost).Handle(new CompletionResolveRequest { Item = completionItem });

        private OmniSharpTestHost GetImportCompletionHost()
        {
            var testHost = CreateOmniSharpHost(configurationData: new[] { new KeyValuePair<string, string>("RoslynExtensionsOptions:EnableImportCompletion", "true") });
            testHost.AddFilesToWorkspace();
            return testHost;
        }

        private static string NormalizeNewlines(string str)
            => str.Replace("\r\n", Environment.NewLine);

        private static void VerifySortOrders(IReadOnlyList<CompletionItem> items)
        {
            Assert.All(items, c =>
            {
                Assert.True(c.SortText.StartsWith("0") || c.SortText.StartsWith("1"));
            });
        }
    }

    internal static class CompletionResponseExtensions
    {
        public static bool IsSuggestionMode(this CompletionItem item) => !item.CommitCharacters?.Contains(' ') ?? true;
    }
}
