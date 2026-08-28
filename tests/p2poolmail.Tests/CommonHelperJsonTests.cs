using p2poolmail;

namespace p2poolmail.Tests;

public class CommonHelperJsonTests
{
    [Fact]
    public void TryReadJsonField_String_Found()
    {
        var ok = CommonHelper.TryReadJsonField("{\"worker\":\"rig-01\",\"hr\":123}", "worker", out string value);
        Assert.True(ok);
        Assert.Equal("rig-01", value);
    }

    [Fact]
    public void TryReadJsonField_String_Missing_ReturnsFalse()
    {
        var ok = CommonHelper.TryReadJsonField("{\"worker\":\"rig-01\"}", "hashrate", out string value);
        Assert.False(ok);
        Assert.Equal(string.Empty, value);
    }

    [Fact]
    public void TryReadJsonField_String_NestedObject_Found()
    {
        var ok = CommonHelper.TryReadJsonField("{\"a\":{\"b\":\"deep\"}}", "b", out string value);
        Assert.True(ok);
        Assert.Equal("deep", value);
    }

    [Fact]
    public void TryReadJsonField_EmptyInputs_ReturnsFalse()
    {
        Assert.False(CommonHelper.TryReadJsonField("", "f", out string _));
        Assert.False(CommonHelper.TryReadJsonField("   ", "f", out string _));
        Assert.False(CommonHelper.TryReadJsonField("{\"a\":1}", "", out string _));
        Assert.False(CommonHelper.TryReadJsonField((ReadOnlySpan<char>)default, "f", out string _));
    }

    [Fact]
    public void ReadJsonField_Int_Found()
    {
        var ok = CommonHelper.ReadJsonField("{\"hashrate_15m\":27109}", "hashrate_15m", out int value);
        Assert.True(ok);
        Assert.Equal(27109, value);
    }

    [Fact]
    public void ReadJsonField_Int_WrongType_ReturnsFalse()
    {
        var ok = CommonHelper.ReadJsonField("{\"hashrate\":\"fast\"}", "hashrate", out int value);
        Assert.False(ok);
        Assert.Equal(0, value);
    }

    [Fact]
    public void ReadJsonField_Int_NegativeNumber_Found()
    {
        var ok = CommonHelper.ReadJsonField("{\"workers\":-2}", "workers", out int value);
        Assert.True(ok);
        Assert.Equal(-2, value);
    }

    [Fact]
    public void TryReadJsonField_StringArray_Found()
    {
        var ok = CommonHelper.TryReadJsonField("{\"tags\":[\"a\",\"b\",\"c\"]}", "tags", out string[] value);
        Assert.True(ok);
        Assert.Equal(new[] { "a", "b", "c" }, value);
    }

    [Fact]
    public void TryReadJsonField_StringArray_EmptyArray_ReturnsTrueWithEmpty()
    {
        var ok = CommonHelper.TryReadJsonField("{\"tags\":[]}", "tags", out string[] value);
        Assert.True(ok);
        Assert.Empty(value);
    }

    [Fact]
    public void TryReadJsonField_StringArray_WrongType_ReturnsFalse()
    {
        var ok = CommonHelper.TryReadJsonField("{\"tags\":\"single\"}", "tags", out string[] value);
        Assert.False(ok);
        Assert.Empty(value);
    }

    [Fact]
    public void TryReadJsonField_UnicodeEscapes_Decoded()
    {
        var ok = CommonHelper.TryReadJsonField("{\"name\":\"\\u00e9\\u4f60\\u597d\"}", "name", out string value);
        Assert.True(ok);
        Assert.Equal("é你好", value);
    }
}
