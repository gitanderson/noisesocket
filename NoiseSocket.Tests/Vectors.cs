using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Noise.Tests
{
	internal static class Vectors
	{
		private const string PrologueHex = "4a6f686e2047616c74";
		private static readonly byte[] PrologueRaw = Hex.Decode(PrologueHex);

		private const string AliceStaticHex = "e61ef9919cde45dd5f82166404bd08e38bceb5dfdfded0a34c8df7ed542214d1";
		private static readonly byte[] AliceStaticRaw = Hex.Decode(AliceStaticHex);

		private const string AliceStaticPublicHex = "6bc3822a2aa7f4e6981d6538692b3cdf3e6df9eea6ed269eb41d93c22757b75a";
		private static readonly byte[] AliceStaticPublicRaw = Hex.Decode(AliceStaticPublicHex);

		private const string AliceEphemeralHex = "893e28b9dc6ca8d611ab664754b8ceb7bac5117349a4439a6b0569da977c464a";
		private static readonly byte[] AliceEphemeralRaw = Hex.Decode(AliceEphemeralHex);

		private const string BobStaticHex = "4a3acbfdb163dec651dfa3194dece676d437029c62a408b4c5ea9114246e4893";
		private static readonly byte[] BobStaticRaw = Hex.Decode(BobStaticHex);

		private const string BobStaticPublicHex = "31e0303fd6418d2f8c0e78b91f22e8caed0fbe48656dcf4767e4834f701b8f62";
		private static readonly byte[] BobStaticPublicRaw = Hex.Decode(BobStaticPublicHex);

		private const string BobEphemeralHex = "bbdb4cdbd309f1a1f2e1456967fe288cadd6f712d65dc7b7793d5e63da6b375b";
		private static readonly byte[] BobEphemeralRaw = Hex.Decode(BobEphemeralHex);

		private static readonly byte[] InitialNegotiationData = Hex.Decode("4e6f697365536f636b6574");
		private static readonly byte[] SwitchNegotiationData = Hex.Decode("537769746368");
		private static readonly byte[] RetryNegotiationData = Hex.Decode("5265747279");

		private const string Psk = "54686973206973206d7920417573747269616e20706572737065637469766521";
		private static readonly List<string> psksHex = new List<string> { Psk };
		private static readonly List<byte[]> psksRaw = new List<byte[]> { Hex.Decode(Psk) };

		private static readonly List<byte[]> payloads = new List<byte[]>
		{
			Hex.Decode("4c756477696720766f6e204d69736573"),
			Hex.Decode("4d757272617920526f746862617264"),
			Hex.Decode("462e20412e20486179656b"),
			Hex.Decode("4361726c204d656e676572"),
			Hex.Decode("4a65616e2d426170746973746520536179"),
			Hex.Decode("457567656e2042f6686d20766f6e2042617765726b")
		};

		public static async Task<IEnumerable<Vector>> Generate()
		{
			var vectors = new List<Vector>();

			using (var stream = new MemoryStream())
			{
				foreach (var test in GetTests())
				{
					var aliceConfig = new ProtocolConfig(
						initiator: true,
						prologue: PrologueRaw,
						s: test.InitStaticRequired ? AliceStaticRaw : null,
						rs: test.InitRemoteStaticRequired ? BobStaticPublicRaw : null,
						psks: test.PsksRequired ? psksRaw : null
					);

					var bobConfig = new ProtocolConfig(
						initiator: false,
						prologue: PrologueRaw,
						s: test.RespStaticRequired ? BobStaticRaw : null,
						rs: test.RespRemoteStaticRequired ? AliceStaticPublicRaw : null,
						psks: test.PsksRequired ? psksRaw : null
					);

					var alice = new NoiseSocket(test.Protocol, aliceConfig, stream, true);
					var bob = new NoiseSocket(test.Protocol, bobConfig, stream, true);

					alice.SetInitializer(handshakeState => Utilities.SetDh(handshakeState, AliceEphemeralRaw.ToArray()));
					bob.SetInitializer(handshakeState => Utilities.SetDh(handshakeState, BobEphemeralRaw.ToArray()));

					var proxy = new SocketProxy(stream, (ushort)test.PaddedLength);
					var queue = new Queue<byte[]>(payloads);

					var writer = alice;
					var reader = bob;

					if (test.Response == Response.Accept)
					{
						await proxy.WriteHandshakeMessageAsync(alice, InitialNegotiationData, queue.Dequeue());
						await bob.ReadNegotiationDataAsync();
						bob.Accept(test.Protocol, bobConfig);

						await bob.ReadHandshakeMessageAsync();
						stream.Position = 0;

						Utilities.Swap(ref writer, ref reader);
					}
					else if (test.Response == Response.Switch)
					{
						await proxy.WriteHandshakeMessageAsync(alice, InitialNegotiationData, queue.Dequeue());
						await bob.ReadNegotiationDataAsync();

						if (test.Fallback != null)
						{
							await bob.ReadHandshakeMessageAsync();
							stream.Position = 0;

							bob.Switch(test.Fallback, new ProtocolConfig(true, PrologueRaw, BobStaticRaw));

							await proxy.WriteHandshakeMessageAsync(bob, SwitchNegotiationData, queue.Dequeue());
							await alice.ReadNegotiationDataAsync();

							alice.Switch(test.Fallback, new ProtocolConfig(false, PrologueRaw, AliceStaticRaw));

							await alice.ReadHandshakeMessageAsync();
							stream.Position = 0;
						}
						else
						{
							bob.Switch(test.Protocol, aliceConfig);
							bob.SetInitializer(handshakeState => Utilities.SetDh(handshakeState, AliceEphemeralRaw.ToArray()));

							await bob.IgnoreHandshakeMessageAsync();
							stream.Position = 0;

							await proxy.WriteHandshakeMessageAsync(bob, SwitchNegotiationData, queue.Dequeue());
							await alice.ReadNegotiationDataAsync();

							alice.Switch(test.Protocol, bobConfig);
							alice.SetInitializer(handshakeState => Utilities.SetDh(handshakeState, BobEphemeralRaw.ToArray()));

							await alice.ReadHandshakeMessageAsync();
							stream.Position = 0;
						}
					}
					else if (test.Response == Response.Retry)
					{
						await proxy.WriteHandshakeMessageAsync(alice, InitialNegotiationData, queue.Dequeue());
						await bob.ReadNegotiationDataAsync();
						bob.Retry(test.Protocol, bobConfig);

						await bob.IgnoreHandshakeMessageAsync();
						stream.Position = 0;

						await proxy.WriteEmptyHandshakeMessageAsync(bob, RetryNegotiationData);
						await alice.ReadNegotiationDataAsync();
						alice.Retry(test.Protocol, aliceConfig);

						await alice.IgnoreHandshakeMessageAsync();
						stream.Position = 0;
					}

					while (queue.Count > 0)
					{
						if (writer.HandshakeHash.IsEmpty)
						{
							await proxy.WriteHandshakeMessageAsync(writer, null, queue.Dequeue());
							await reader.ReadNegotiationDataAsync();
							await reader.ReadHandshakeMessageAsync();
							stream.Position = 0;
						}
						else
						{
							await proxy.WriteMessageAsync(writer, queue.Dequeue());
						}

						Utilities.Swap(ref writer, ref reader);
					}

					var initialConfig = new Config
					{
						ProtocolName = Encoding.ASCII.GetString(test.Protocol.Name),
						AliceStatic = aliceConfig.LocalStatic,
						AliceEphemeral = AliceEphemeralRaw,
						AliceRemoteStatic = aliceConfig.RemoteStatic,
						BobStatic = bobConfig.LocalStatic,
						BobEphemeral = BobEphemeralRaw,
						BobRemoteStatic = bobConfig.RemoteStatic
					};

					var switchConfig = new Config
					{
						ProtocolName = Encoding.ASCII.GetString(test.Protocol.Name),
						AliceStatic = bobConfig.LocalStatic,
						AliceEphemeral = BobEphemeralRaw,
						AliceRemoteStatic = bobConfig.RemoteStatic,
						BobStatic = aliceConfig.LocalStatic,
						BobEphemeral = AliceEphemeralRaw,
						BobRemoteStatic = aliceConfig.RemoteStatic
					};

					if (test.Fallback != null)
					{
						switchConfig = new Config
						{
							ProtocolName = Encoding.ASCII.GetString(test.Fallback.Name),
							AliceStatic = AliceStaticRaw,
							AliceEphemeral = AliceEphemeralRaw,
							BobStatic = BobStaticRaw,
							BobEphemeral = BobEphemeralRaw
						};
					}

					var vector = new Vector
					{
						Initial = initialConfig,
						Switch = test.Response == Response.Switch ? switchConfig : null,
						Retry = test.Response == Response.Retry ? initialConfig : null,
						AlicePrologue = PrologueHex,
						AlicePsks = test.PsksRequired ? psksHex : null,
						BobPrologue = PrologueHex,
						BobPsks = test.PsksRequired ? psksHex : null,
						HandshakeHash = Hex.Encode(writer.HandshakeHash.ToArray()),
						Messages = proxy.Messages
					};

					alice.Dispose();
					bob.Dispose();

					vectors.Add(vector);
				}
			}

			return vectors;
		}

		private static IEnumerable<(HandshakePattern, PatternModifiers)> Patterns
		{
			get
			{
				yield return (HandshakePattern.NN, PatternModifiers.Psk0 | PatternModifiers.Psk2);
				yield return (HandshakePattern.NK, PatternModifiers.Psk0 | PatternModifiers.Psk2);
				yield return (HandshakePattern.NX, PatternModifiers.Psk2);
				yield return (HandshakePattern.XN, PatternModifiers.Psk3);
				yield return (HandshakePattern.XK, PatternModifiers.Psk3);
				yield return (HandshakePattern.XX, PatternModifiers.Psk3);
				yield return (HandshakePattern.KN, PatternModifiers.Psk0 | PatternModifiers.Psk2);
				yield return (HandshakePattern.KK, PatternModifiers.Psk0 | PatternModifiers.Psk2);
				yield return (HandshakePattern.KX, PatternModifiers.Psk2);
				yield return (HandshakePattern.IN, PatternModifiers.Psk1 | PatternModifiers.Psk2);
				yield return (HandshakePattern.IK, PatternModifiers.Psk1 | PatternModifiers.Psk2);
				yield return (HandshakePattern.IX, PatternModifiers.Psk2);
			}
		}

		private static IEnumerable<CipherFunction> Ciphers
		{
			get
			{
				yield return CipherFunction.AesGcm;
				yield return CipherFunction.ChaChaPoly;
			}
		}

		private static IEnumerable<HashFunction> Hashes
		{
			get
			{
				yield return HashFunction.Sha256;
				yield return HashFunction.Sha512;
				yield return HashFunction.Blake2s;
				yield return HashFunction.Blake2b;
			}
		}

		private static IEnumerable<int> PaddedLengths
		{
			get
			{
				yield return 0;
				yield return 32;
			}
		}

		private static IEnumerable<Response> Responses
		{
			get
			{
				yield return Response.Accept;
				yield return Response.Switch;
				yield return Response.Retry;
			}
		}

		private static IEnumerable<Test> GetTests()
		{
			foreach (var (pattern, modifiers) in Patterns)
			{
				bool initStaticRequired = pattern.LocalStaticRequired(true);
				bool initRemoteStaticRequired = pattern.RemoteStaticRequired(true);

				bool respStaticRequired = pattern.LocalStaticRequired(false);
				bool respRemoteStaticRequired = pattern.RemoteStaticRequired(false);

				foreach (var cipher in Ciphers)
				{
					foreach (var hash in Hashes)
					{
						var fallback = new Protocol(HandshakePattern.XX, cipher, hash, PatternModifiers.Fallback);

						foreach (PatternModifiers modifier in Enum.GetValues(typeof(PatternModifiers)))
						{
							if (!modifiers.HasFlag(modifier))
							{
								continue;
							}

							var protocol = new Protocol(pattern, cipher, hash, modifier);
							var name = Encoding.ASCII.GetString(protocol.Name);
							var psksRequired = modifier != PatternModifiers.None;

							foreach (var paddedLength in PaddedLengths)
							{
								foreach (var response in Responses)
								{
									yield return new Test
									{
										Protocol = protocol,
										InitStaticRequired = initStaticRequired,
										InitRemoteStaticRequired = initRemoteStaticRequired,
										RespStaticRequired = respStaticRequired,
										RespRemoteStaticRequired = respRemoteStaticRequired,
										PsksRequired = psksRequired,
										PaddedLength = paddedLength,
										Response = response
									};

									if (response == Response.Switch)
									{
										yield return new Test
										{
											Protocol = protocol,
											Fallback = fallback,
											InitStaticRequired = initStaticRequired,
											InitRemoteStaticRequired = initRemoteStaticRequired,
											RespStaticRequired = respStaticRequired,
											RespRemoteStaticRequired = respRemoteStaticRequired,
											PsksRequired = psksRequired,
											PaddedLength = paddedLength,
											Response = response
										};
									}
								}
							}
						}
					}
				}
			}
		}
	}
}
