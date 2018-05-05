using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Noise.Tests
{
	public class NoiseSocketTest
	{
		[Fact]
		public async Task TestVectors()
		{
			var s = File.ReadAllText("Vectors/noisesocket.json");
			var json = JObject.Parse(s);

			using (var stream = new MemoryStream())
			{
				foreach (var vector in json["vectors"])
				{
					var protocolName = GetString(vector, "protocol_name");
					var initPrologue = GetBytes(vector, "init_prologue");
					var initStatic = GetBytes(vector, "init_static");
					var initEphemeral = GetBytes(vector, "init_ephemeral");
					var initRemoteStatic = GetBytes(vector, "init_remote_static");
					var respPrologue = GetBytes(vector, "resp_prologue");
					var respStatic = GetBytes(vector, "resp_static");
					var respEphemeral = GetBytes(vector, "resp_ephemeral");
					var respRemoteStatic = GetBytes(vector, "resp_remote_static");
					var handshakeHash = GetBytes(vector, "handshake_hash");

					var initConfig = new ProtocolConfig(true, initPrologue, initStatic, initRemoteStatic);
					var respConfig = new ProtocolConfig(false, respPrologue, respStatic, respRemoteStatic);

					var protocol = Protocol.Parse(protocolName.AsSpan());
					var isOneWay = protocol.HandshakePattern.Patterns.Count() == 1;

					var initSocket = NoiseSocket.CreateClient(protocol, initConfig, stream, true);
					var respSocket = NoiseSocket.CreateServer(stream, true);

					initSocket.SetInitializer(handshakeState => Utilities.SetDh(handshakeState, initEphemeral));
					respSocket.SetInitializer(handshakeState => Utilities.SetDh(handshakeState, respEphemeral));

					int index = 0;

					foreach (var message in vector["messages"])
					{
						stream.Position = 0;

						var negotiationData = GetBytes(message, "negotiation_data");
						var messageBody = GetBytes(message, "message_body");
						var paddedLength = (ushort?)message["padded_length"] ?? 0;
						var value = GetBytes(message, "message");

						if (initSocket.HandshakeHash.IsEmpty)
						{
							await initSocket.WriteHandshakeMessageAsync(negotiationData, messageBody, paddedLength);
							var initMessage = Utilities.ReadMessageRaw(stream);
							Assert.Equal(value, initMessage);

							stream.Position = 0;
							var respNegotiationData = await respSocket.ReadNegotiationDataAsync();
							Assert.Equal(negotiationData, respNegotiationData);

							if (index == 0)
							{
								respSocket.Accept(protocol, respConfig);
							}

							var respMessageBody = await respSocket.ReadHandshakeMessageAsync();
							Assert.Equal(messageBody, respMessageBody);
						}
						else if (isOneWay && index == 1)
						{
							await respSocket.WriteEmptyHandshakeMessageAsync();
							var respMessage = Utilities.ReadMessageRaw(stream);
							Assert.Equal(value, respMessage);
						}
						else
						{
							await initSocket.WriteMessageAsync(messageBody, paddedLength);
							var initMessage = Utilities.ReadMessageRaw(stream);
							Assert.Equal(value, initMessage);

							stream.Position = 0;
							var respMessageBody = await respSocket.ReadMessageAsync();
							Assert.Equal(messageBody, respMessageBody);
						}

						if (!isOneWay)
						{
							var temp = initSocket;
							initSocket = respSocket;
							respSocket = temp;
						}

						++index;
					}

					Assert.Equal(handshakeHash, initSocket.HandshakeHash.ToArray());
					Assert.Equal(handshakeHash, respSocket.HandshakeHash.ToArray());

					initSocket.Dispose();
					respSocket.Dispose();
				}
			}
		}

		private static string GetString(JToken token, string property)
		{
			return (string)token[property] ?? String.Empty;
		}

		private static byte[] GetBytes(JToken token, string property)
		{
			return Hex.Decode(GetString(token, property));
		}
	}
}
