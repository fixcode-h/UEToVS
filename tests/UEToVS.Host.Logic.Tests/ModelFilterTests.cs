using UEToVS.Host.Logic.Agent;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    public class ModelFilterTests
    {
        private static readonly string[] Pool = { "claude-haiku-5.5", "deepseek-v4-flash", "deepseek-v4-pro", "deepseek-v4.1-flash-eco", "glm-5.3-flash", "mimo-v2.6-pro" };

        [Fact]
        public void EmptyQueryShowsEverything() => Assert.Equal(Pool.Length, ModelFilter.Apply(Pool, "  ").Count);

        [Fact]
        public void SubstringIgnoresCase() => Assert.Equal(new[] { "deepseek-v4-flash", "deepseek-v4.1-flash-eco", "glm-5.3-flash" }, ModelFilter.Apply(Pool, "FLASH"));

        [Fact]
        public void EveryWordMustMatch() => Assert.Equal(new[] { "deepseek-v4-pro" }, ModelFilter.Apply(Pool, "deep pro"));

        [Fact]
        public void SeparatorsAreIgnored()
        {
            Assert.Equal(new[] { "deepseek-v4.1-flash-eco" }, ModelFilter.Apply(Pool, "v41"));
            Assert.Equal(new[] { "glm-5.3-flash" }, ModelFilter.Apply(Pool, "glm53"));
        }

        [Fact]
        public void NoMatchIsEmpty() => Assert.Empty(ModelFilter.Apply(Pool, "qwen"));

        [Fact]
        public void EnterAddsTheSingleMatchOtherwiseTheTypedText()
        {
            Assert.Equal("mimo-v2.6-pro", ModelFilter.ResolveEnter(Pool, "mimo"));
            Assert.Equal("deepseek", ModelFilter.ResolveEnter(Pool, "deepseek"));
            Assert.Equal("my-custom-model", ModelFilter.ResolveEnter(Pool, " my-custom-model "));
            Assert.Equal("glm-5.3-flash", ModelFilter.ResolveEnter(Pool, "GLM-5.3-FLASH"));
            Assert.Null(ModelFilter.ResolveEnter(Pool, "   "));
            Assert.Equal("x", ModelFilter.ResolveEnter(null, "x"));
        }
    }
}
