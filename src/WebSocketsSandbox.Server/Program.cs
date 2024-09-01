using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

string ip = "127.0.0.1";
int port = 8082;
TcpListener server = new(IPAddress.Parse(ip), port);

server.Start();
Console.WriteLine($"Server has started on {ip}:{port}.\nWaiting for a connection...");

TcpClient client = server.AcceptTcpClient();
Console.WriteLine("A client connected.");

NetworkStream stream = client.GetStream();

// Enter to an infinite cycle to be able to handle every change in stream
while (true)
{
    while (!stream.DataAvailable) ; // Wait for data to be available.
    while (client.Available < 3) ;  // Wait for enough bytes to be available.

    byte[] bytes = new byte[client.Available];
    stream.Read(bytes, 0, bytes.Length);
    string data = Encoding.UTF8.GetString(bytes);

    if (Regex.IsMatch(data, "^GET", RegexOptions.IgnoreCase))
    {
        Console.WriteLine($"===== Handshaking from client =====\n{data}");

        // 1. Obtain the value of the "Sec-WebSocket-Key" request header without any leading or trailing whitespace.
        // 2. Concatenate it with "258EAFA5-E914-47DA-95CA-C5AB0DC85B11" (a special GUID specified by RFC 6455)
        // 3. Compute SHA-1 and Base64 hash of the new value.
        // 4. Write the hash back as the value of "Sec-WebSocket-Accept" response header in an HTTP response.

        string secWebSocketKey = Regex.Match(data, "Sec-WebSocket-Key: (.*)").Groups[1].Value.Trim();
        string secWebSocketAcceptConcat = $"{secWebSocketKey}258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
        byte[] secWebSocketAcceptSha1 = SHA1.Create().ComputeHash(Encoding.UTF8.GetBytes(secWebSocketAcceptConcat));
        string secWebSocketAccept = Convert.ToBase64String(secWebSocketAcceptSha1);

        string responseStr =
            $"HTTP/1.1 101 Switching Protocols\r\n" +
            $"Connection: Upgrade\r\n" +
            $"Upgrade: websocket\r\n" +
            $"Sec-WebSocket-Accept: {secWebSocketAccept}\r\n\r\n";

        byte[] response = Encoding.UTF8.GetBytes(responseStr);
        stream.Write(response, 0, response.Length);
    }
    else
    {
        bool fin = (bytes[0] & 0b10000000) != 0;
        bool mask = (bytes[1] & 0b10000000) != 0; // Must be true, "All messages from the client to the server have this bit set".
        int opcode = bytes[0] & 0b00001111; // Expecting 1 - text message.
        ulong offset = 2;
        ulong msgLen = bytes[1] & (ulong)0b01111111;

        // Close frame
        if (opcode == 8)
        {
            client.Close();
            Console.WriteLine("A client disconnected.");

            client = server.AcceptTcpClient();
            stream = client.GetStream();
        }

        if (msgLen == 126)
        {
            // bytes are reversed because websocket will print them in Big-Endian, whereas
            // BitConverter will want them arranged in little-endian on windows.
            msgLen = BitConverter.ToUInt16([bytes[3], bytes[2]], 0);
            offset = 4;
        }
        else if (msgLen == 127)
        {
            // To test the below code, we need to manually buffer larger messages - since the NIC's auto-buffering
            // may be too latency-friendly for this code to run (that is, we may have only some of the bytes in this
            // websocket frame available through client.Available).
            msgLen = BitConverter.ToUInt64([bytes[9], bytes[8], bytes[7], bytes[6], bytes[5], bytes[4], bytes[3], bytes[2]], 0);
            offset = 10;
        }

        if (msgLen == 0)
        {
            Console.WriteLine("msgLen == 0");
        }
        else if (mask)
        {
            byte[] decoded = new byte[msgLen];
            byte[] masks = [bytes[offset], bytes[offset + 1], bytes[offset + 2], bytes[offset + 3]];
            offset += 4;

            for (ulong i = 0; i < msgLen; i++)
            {
                decoded[i] = (byte)(bytes[offset + i] ^ masks[i % 4]);
            }

            string text = Encoding.UTF8.GetString(decoded);
            Console.WriteLine(text);

            byte[] serverMessage = CreateWebSocketFrame(decoded, true);
            stream.Write(serverMessage, 0, serverMessage.Length);
        }
        else
        {
            Console.WriteLine("mask bit not set");
        }
        Console.WriteLine();
    }
}

byte[] CreateMessage(string message, string name = "WebSockets Server")
{
    var msgObj = new
    {
        Type = "message",
        Name = name,
        Text = message,
        Date = DateTimeOffset.Now.ToUnixTimeSeconds(),
    };

    string msgStr = JsonSerializer.Serialize(msgObj, new JsonSerializerOptions()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    });

    byte[] result = Encoding.UTF8.GetBytes(msgStr);
    return result;
}

byte[] CreateWebSocketFrame(byte[] message, bool isFinalFrame)
{
    int payloadLength = message.Length;
    byte[] frame;
    int frameStartIndex;

    if (payloadLength <= 125)
    {
        frame = new byte[2 + payloadLength];
        frameStartIndex = 2;
        frame[1] = (byte)payloadLength;
    }
    else if (payloadLength <= 65535)
    {
        frame = new byte[4 + payloadLength];
        frameStartIndex = 4;
        frame[1] = 126;
        frame[2] = (byte)(payloadLength >> 8);  // Most significant byte
        frame[3] = (byte)(payloadLength & 0xFF); // Least significant byte
    }
    else
    {
        frame = new byte[10 + payloadLength];
        frameStartIndex = 10;
        frame[1] = 127;
        for (int i = 0; i < 8; i++)
        {
            frame[9 - i] = (byte)(payloadLength >> (8 * i));
        }
    }

    frame[0] = (byte)(0b10000000 | 0b00000001); // Opcode for text
    if (!isFinalFrame)
    {
        frame[0] &= 0b01111111; // Clear the FIN bit if not final
    }

    Array.Copy(message, 0, frame, frameStartIndex, payloadLength);
    return frame;
}
