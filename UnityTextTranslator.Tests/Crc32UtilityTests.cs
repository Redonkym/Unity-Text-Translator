using System.Text;
using UnityTextTranslator;
using Xunit;

namespace UnityTextTranslator.Tests
{
    /// <summary>
    /// Crc32Utility используется для обнуления CRC записей провайдера AssetBundle в каталоге Addressables
    /// (приём «patchcrc»). Сверяем со стандартными эталонными контрольными величинами CRC-32 (ISO-HDLC):
    /// ошибка в таблице/инициализации/финальном XOR сразу всплывёт.
    /// </summary>
    public class Crc32UtilityTests
    {
        [Fact]
        public void Empty_input_is_zero()
        {
            Assert.Equal(0u, Crc32Utility.Compute(new byte[0]));
        }

        [Theory]
        [InlineData("123456789", 0xCBF43926u)] // каноническая контрольная величина CRC-32
        [InlineData("The quick brown fox jumps over the lazy dog", 0x414FA339u)]
        public void Known_vectors_match_reference_crc32(string text, uint expected)
        {
            Assert.Equal(expected, Crc32Utility.Compute(Encoding.ASCII.GetBytes(text)));
        }
    }
}
