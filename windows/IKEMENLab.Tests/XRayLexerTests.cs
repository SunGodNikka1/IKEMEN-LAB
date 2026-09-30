using IKEMENLab.Core.XRay.Source;
using Xunit;

namespace IKEMENLab.Tests;

public class XRayLexerTests
{
    [Fact]
    public void MixedLineEndingsKeepTrueLineNumbers()
    {
        var lex = SourceLexer.Lex("[A]\r\nk = 1\n[B]\rj = 2\r\n\r\n[C]\nz = 3", 7);
        Assert.Equal(["A", "B", "C"], lex.Blocks.Select(b => b.Header));
        Assert.Equal([1, 3, 6], lex.Blocks.Select(b => b.HeaderLine));
        Assert.Equal(4, lex.Blocks[1].Entries[0].Line);
        Assert.Equal(7, lex.Blocks[2].Entries[0].Line);
    }

    [Fact]
    public void CommentsInsideQuotesAreData_AndTrailingCommentIsKept()
    {
        var lex = SourceLexer.Lex("[State 1]\nvalue = \"a;b\" ; note\n", 0);
        var e = lex.Blocks[0].Entries[0];
        Assert.Equal("\"a;b\"", e.Value);
        Assert.Equal(" note", e.TrailingComment);
    }

    [Fact]
    public void LeadingCommentsAboveAHeaderAreCaptured_BlankLineEndsTheRun()
    {
        var lex = SourceLexer.Lex("; far away\n\n; Love Train\n; second\n[Statedef 3000]\ntype = S\n", 0);
        Assert.Equal(["Love Train", "second"], lex.Blocks[0].LeadingComments);
    }

    [Fact]
    public void BomEmptyNamesStrayTextAndMissingEqualsNeverThrow()
    {
        var lex = SourceLexer.Lex("﻿[State ]\ntrigger1 = 1\nthis is stray\n  \tvalue=   5\n[Unclosed\nx=1", 0);
        Assert.Equal(["State", "Unclosed"], lex.Blocks.Select(b => b.Header));
        Assert.Equal("this is stray", lex.Blocks[0].Entries[1].Key);
        Assert.False(lex.Blocks[0].Entries[1].HadEquals);
        Assert.Equal("5", lex.Blocks[0].Entries[2].Value);
        Assert.Contains(lex.Diagnostics, d => d.Code == "lex.unclosed-bracket" && d.Source!.Value.StartLine == 5);
    }

    [Fact]
    public void LinesBeforeTheFirstHeaderFormAPreambleBlock()
    {
        var lex = SourceLexer.Lex("orphan = 1\n[A]\n", 0);
        Assert.Equal(string.Empty, lex.Blocks[0].Header);
        Assert.Equal("A", lex.Blocks[1].Header);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n\n\n")]
    [InlineData("[")]
    [InlineData("=")]
    [InlineData(";;;;")]
    [InlineData("[[[]]]\n=\n\"")]
    public void DegenerateInputIsSafe(string text) => Assert.NotNull(SourceLexer.Lex(text, 0));
}
