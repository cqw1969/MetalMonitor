using System.Text;

// 标准测试向量："123456789" 的 ASCII 字节（0x31~0x39，共 9 个字节）
//byte[] testVector = Encoding.ASCII.GetBytes("123456789");
byte[] testVector = { 0x06 ,0x01 ,0x00 ,0x01 ,0x77, 0x03 ,0x01 };

ushort result = ComputeCrc16(testVector);

Console.WriteLine($"\"06 01 00 01 77 03 01\" -> 0x{result:X4}  (期望 0x070C)");
Console.WriteLine(result == 0x070C ? "PASS" : "FAIL");

// 附加验证：空数据应原样返回初值
Console.WriteLine($"空数据 -> 0x{ComputeCrc16(Array.Empty<byte>()):X4}  (期望 0xFFFF)");

static ushort ComputeCrc16(byte[] data)
{
    ushort crc = 0xFFFF;
    foreach (byte b in data)
    {
        crc ^= b;
        for (int i = 0; i < 8; i++)
        {
            if ((crc & 0x0001) != 0)
            {
                crc = (ushort)((crc >> 1) ^ 0xA001);
            }
            else
            {
                crc >>= 1;
            }
        }
    }
    return crc;
}
