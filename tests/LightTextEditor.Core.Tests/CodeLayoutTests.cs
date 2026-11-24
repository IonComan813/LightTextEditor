using LightTextEditor.Core.Text;

namespace LightTextEditor.Core.Tests;

public class CodeLayoutTests
{
    private static string Expand(CodeLanguage language, string text)
    {
        var result = CodeLayout.Expand(language, text, "  ", "\n");
        Assert.True(result.Succeeded, result.Error);
        return result.Text!;
    }

    private static LayoutResult Compact(CodeLanguage language, string text) => CodeLayout.Compact(language, text, "\n");

    [Theory]
    [InlineData(".json", CodeLanguage.Json)]
    [InlineData(".CSS", CodeLanguage.Css)]
    [InlineData(".mjs", CodeLanguage.JavaScript)]
    [InlineData(".txt", null)]   // 其他文件都不支持，内容像 JSON 也不算
    [InlineData(".log", null)]
    [InlineData(".html", null)]
    [InlineData("", null)]
    public void LanguageFor(string extension, CodeLanguage? expected) =>
        Assert.Equal(expected, CodeLayout.LanguageFor(extension));

    [Fact]
    public void Compact_KeepsCommentsAndNames()
    {
        // 压缩只去掉空白，不删注释、不改变量名
        Assert.Equal("/* 说明 */ a{color:red}", CodeLayout.Compact(CodeLanguage.Css, "/* 说明 */\na {\n  color:red\n}\n").Text);
        // （注释后的换行保留：可能影响自动插入分号）
        Assert.Equal("/* 说明 */\nfunction add(first,second){return first + second}",
            CodeLayout.Compact(CodeLanguage.JavaScript, "/* 说明 */\nfunction add(first, second) {\n  return first + second\n}\n").Text);
    }

    // ───────────── CSS ─────────────

    private const string MinifiedCss =
        "a{color:red;background:url(data:x;y)}b:hover,i>p{}@media (max-width:1px){c{d:e;f:\"g;{}\"}}/*x;y*/h{k:l}";

    [Fact]
    public void Css_Expand()
    {
        const string expected = """
            a {
              color:red;
              background:url(data:x;y)
            }
            b:hover,i>p {}
            @media (max-width:1px) {
              c {
                d:e;
                f:"g;{}"
              }
            }
            /*x;y*/h {
              k:l
            }
            """;
        Assert.Equal(expected.Replace("\r\n", "\n"), Expand(CodeLanguage.Css, MinifiedCss));
    }

    [Fact]
    public void Css_CompactRestoresTheMinifiedText()
    {
        var result = Compact(CodeLanguage.Css, Expand(CodeLanguage.Css, MinifiedCss));
        Assert.Equal(MinifiedCss, result.Text);
        Assert.Equal(0, result.KeptLineBreaks);
    }

    [Fact]
    public void Css_Compact_KeepsSignificantSpaces() =>
        // 后代选择器的空格、calc 里运算符两边的空格必须保留；a :hover 和 a:hover 含义不同，不动
        Assert.Equal("div :hover{width:calc(1px + 2px)}", Compact(CodeLanguage.Css, "div :hover {\n  width: calc(1px + 2px)\n}\n").Text!.Replace(": ", ":"));

    [Fact]
    public void Css_CustomPropertiesAndVarValues_AreKeptAsWritten()
    {
        // 浏览器按原文保存这些值，逗号后的空格也算值的一部分
        const string css = ":root{--font: a, \"b\";--block: { x: y }}p{box-shadow:var(--a), 0 0 1px red}";
        var expanded = Expand(CodeLanguage.Css, css);
        Assert.Contains("--font: a, \"b\";", expanded);
        Assert.Contains("--block: { x: y }\n", expanded);
        Assert.Contains("box-shadow:var(--a), 0 0 1px red", expanded);
        Assert.Equal(css, Compact(CodeLanguage.Css, expanded).Text);
    }

    [Theory]
    [InlineData("a{color:red")]
    [InlineData("a{content:\"x}")]
    [InlineData("a{b:c}}")]
    public void Css_RefusesBrokenInput(string css) => Assert.False(CodeLayout.Expand(CodeLanguage.Css, css, "  ", "\n").Succeeded);

    // ───────────── JavaScript ─────────────

    private const string MinifiedJs =
        """!function(e){"use strict";var t={a:1,b:"x;{}"},n=/[;{}]/g;function r(){for(var i=0;i<3;i++){if(i)continue;else break}try{t.a++}catch(o){}finally{}return`${t.a}{;}`+(1/2)}e.r=r;if(e)/a;b/.test("x")}(window);""";

    [Fact]
    public void Js_Expand_BreaksOnlyOutsideStringsRegexesAndTemplates()
    {
        const string expected = """
            !function(e){
              "use strict";
              var t={
                a:1,
                b:"x;{}"
              },
              n=/[;{}]/g;
              function r(){
                for(var i=0;i<3;i++){
                  if(i)continue;
                  else break
                }
                try{
                  t.a++
                } catch(o){} finally{}
                return`${t.a}{;}`+(1/2)
              }
              e.r=r;
              if(e)/a;b/.test("x")
            }(window);
            """;
        Assert.Equal(expected.Replace("\r\n", "\n"), Expand(CodeLanguage.JavaScript, MinifiedJs));
    }

    [Fact]
    public void Js_Compact_AddsSemicolonsOnlyWhereANewStatementFollowsABrace()
    {
        var result = Compact(CodeLanguage.JavaScript, Expand(CodeLanguage.JavaScript, MinifiedJs));
        Assert.Equal(0, result.KeptLineBreaks);
        // 与原文只差 } 和新语句之间的分号（自动插入分号也会在这里断句）
        Assert.Equal(MinifiedJs, result.Text!.Replace("};", "}"));
        Assert.Contains("};try{", result.Text);
        Assert.Contains("}(window);", result.Text); // } 后接 ( 是调用，不能加分号
    }

    [Fact]
    public void Js_ModulesAndClasses()
    {
        const string src = "import{a,b}from\"x\";export default class A extends B{m(){return{x:1}}static n(){}}const f=()=>({a:[1,2]});do{x--}while(x>0)";
        const string expected = """
            import{
              a,
              b
            } from"x";
            export default class A extends B{
              m(){
                return{
                  x:1
                }
              }
              static n(){}
            }
            const f=()=>({
              a:[1,2]
            });
            do{
              x--
            } while(x>0)
            """;
        var expanded = Expand(CodeLanguage.JavaScript, src);
        Assert.Equal(expected.Replace("\r\n", "\n"), expanded);
        var compact = Compact(CodeLanguage.JavaScript, expanded).Text!;
        Assert.Contains("import{a,b}from\"x\";", compact); // from 前不能加分号
        Assert.Contains("}while(x>0)", compact);
        Assert.Equal(src, compact.Replace("};", "}"));
    }

    [Fact]
    public void Js_Compact_KeepsLineBreaksThatMatter()
    {
        // 省略分号的写法：换行就是语句的分隔，保留；return 后的换行、行注释后的换行也保留
        const string src = "const a = 1\nconst b = {x: 1}\nfoo() // 注释\nlet c = a\n  .map(x => x)\nreturn\nx++\n";
        var result = Compact(CodeLanguage.JavaScript, src);
        Assert.Equal("const a = 1\nconst b ={x: 1};foo()// 注释\nlet c = a.map(x => x)\nreturn\nx++", result.Text);
        Assert.Equal(4, result.KeptLineBreaks);
    }

    [Theory]
    [InlineData("var a=\"x;{}")]          // 没有结束的字符串
    [InlineData("var a=`x${b`")]          // 没有结束的模板字符串
    [InlineData("function(){")]
    [InlineData("a=(1]")]
    [InlineData("x=<div>a;b</div>")]      // JSX
    [InlineData("/* 没有结束的注释")]
    public void Js_RefusesWhatItCannotParseReliably(string js)
    {
        var result = CodeLayout.Expand(CodeLanguage.JavaScript, js, "  ", "\n");
        Assert.False(result.Succeeded);
        Assert.False(string.IsNullOrEmpty(result.Error));
        Assert.False(CodeLayout.CanExpand(CodeLanguage.JavaScript, js));
    }

    [Fact]
    public void Js_Compact_AddsASemicolonAfterAnArrowFunctionBody()
    {
        // 箭头函数后面不能接 [ ( 和运算符：原文在这里自动插入了分号，压缩成一行时要补上
        const string src = "const f = () => {\n  return 1\n}\n\n[a, b].forEach(g)\nconst h = function () {\n}\n(x)\n";
        var result = Compact(CodeLanguage.JavaScript, src);
        // 普通函数后面接 ( 是调用（换行时也不会断句），不能加分号
        Assert.Equal("const f =()=>{return 1};[a,b].forEach(g)\nconst h = function(){}(x)", result.Text);
    }

    [Fact]
    public void Js_RegexInsideATemplateInterpolation()
    {
        // ${} 里的正则含有引号：要按正则跳过，否则会把引号当成字符串的开头
        const string src = """f=e=>`"${e.replace(/["\\]/g,x=>`\\`+x)}"`;g()""";
        Assert.Equal(src.Replace(";g", ";\ng"), Expand(CodeLanguage.JavaScript, src));
    }

    [Theory]
    [InlineData("x=e<<1;y=1", "x=e<<1;\ny=1")]       // << 不是 JSX
    [InlineData("x=i++/2;y=/a;b/", "x=i++/2;\ny=/a;b/")] // i++ 之后的 / 是除号；= 之后的是正则
    public void Js_Operators(string src, string expected) =>
        Assert.Equal(expected, Expand(CodeLanguage.JavaScript, src));

    [Fact]
    public void Js_DivisionIsNotARegex() =>
        // a/b/c 是两次除法：如果误当成正则，后面的 ; 就不会换行
        Assert.Equal("x=a/b/c;\ny=1", Expand(CodeLanguage.JavaScript, "x=a/b/c;y=1"));

    [Fact]
    public void Js_ShebangIsKept() =>
        Assert.Equal("#!/usr/bin/env node\nfoo();\nbar()", Expand(CodeLanguage.JavaScript, "#!/usr/bin/env node\nfoo();bar()"));

    [Fact]
    public void CanExpand_DoesNotNeedTheResult()
    {
        Assert.True(CodeLayout.CanExpand(CodeLanguage.JavaScript, MinifiedJs));
        Assert.True(CodeLayout.CanExpand(CodeLanguage.Css, MinifiedCss));
        Assert.True(CodeLayout.CanExpand(CodeLanguage.Json, "[1]"));
        Assert.False(CodeLayout.CanExpand(CodeLanguage.Json, "[1"));
    }

    [Fact]
    public void Compact_UsesTheGivenNewLineForKeptBreaks() =>
        Assert.Equal("a = 1\r\nb = 2", CodeLayout.Compact(CodeLanguage.JavaScript, "a = 1\nb = 2", "\r\n").Text);
}
