﻿//-----------------------------------------------------------------------------
// Filename: MultiSourceAudioSession.cs
//
// Description: A lightweight audio only RTP session suitable for testing.
// No rendering or capturing capabilities.
//
// Author(s):
// Aaron Clauson (aaron@sipsorcery.com)
//
// History:
// 19 Mar 2020	Aaron Clauson	Created, Dublin, Ireland.
// 21 Apr 2020  Aaron Clauson   Added alaw and mulaw decode classes.
// 31 May 2020  Aaron Clauson   Refactored codecs and signal generator to 
//                              separate class files.
// 19 Aug 2020  Aaron Clauson   Renamed from RtpAudioSession to
//                              MultiSourceAudioSession.
//
// License: 
// BSD 3-Clause "New" or "Revised" License, see included LICENSE.md file.
//-----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions.V1;

namespace SIPSorcery.Media
{
    public enum AudioSourcesEnum
    {
        /// <summary>
        /// Plays music samples from a file. No transcoding option is available
        /// so the file format must match the selected codec.
        /// </summary>
        Music = 0,

        /// <summary>
        /// Send an audio stream of silence. Note this option does result
        /// in audio RTP packet getting sent.
        /// </summary>
        Silence = 1,

        /// <summary>
        /// White noise static.
        /// </summary>
        WhiteNoise = 2,

        /// <summary>
        /// A continuous sine wave.
        /// </summary>
        SineWave = 3,

        /// <summary>
        /// Pink noise static.
        /// </summary>
        PinkNoise = 4,

        /// <summary>
        /// Don't send audio RTP packets.
        /// </summary>
        None = 5,

        /// <summary>
        /// Use this option for audio capture devices such as a microphone.
        /// </summary>
        CaptureDevice = 6,
    }

    public class AudioSourceOptions
    {
        /// <summary>
        /// The type of audio source to use.
        /// </summary>
        public AudioSourcesEnum AudioSource;

        /// <summary>
        /// If using a pre-recorded audio source this is the audio source file.
        /// </summary>
        public Dictionary<SDPMediaFormatsEnum, string> SourceFiles;

        /// <summary>
        /// The sampling rate for the audio capture device.
        /// </summary>
        public AudioSamplingRatesEnum CaptureDeviceSampleRate = AudioSamplingRatesEnum.Rate8KHz;
    }

    /// <summary>
    /// An audio only RTP session that can supply an audio stream to the caller. Any incoming audio stream is 
    /// ignored and this class does NOT use any audio devices on the system for capture or playback.
    /// </summary>
    public class MultiSourceAudioSession : PlatformMediaSession
    {
        private const int G722_BIT_RATE = 64000;              // G722 sampling rate is 16KHz with bits per sample of 16.
        private const int AUDIO_SAMPLE_PERIOD_MILLISECONDS = 20;
        private static readonly byte PCMU_SILENCE_BYTE_ZERO = 0x7F;
        private static readonly byte PCMU_SILENCE_BYTE_ONE = 0xFF;
        private static readonly byte PCMA_SILENCE_BYTE_ZERO = 0x55;
        private static readonly byte PCMA_SILENCE_BYTE_ONE = 0xD5;
        private static float LINEAR_MAXIMUM = 32767f;

        private static ILogger Log = SIPSorcery.Sys.Log.Logger;

        private StreamReader _audioStreamReader;
        private SignalGenerator _signalGenerator;
        private Timer _audioStreamTimer;
        private AudioSourceOptions _audioOpts;
        private SDPMediaFormat _sendingFormat;          // The codec that we've selected to send with (must be supported by remote party).
        private int _sendingAudioSampleRate;            // 8KHz for G711, 16KHz for G722.
        private int _sendingAudioRtpRate;               // 8Khz for both G711 and G722. Future codecs could have different values.
        private bool _streamSendInProgress;             // When a send for stream is in progress it takes precedence over the existing audio source.
        private byte[] _silenceBuffer;                  // PCMU and PCMA have a standardised silence format. When using these codecs the buffer can be constructed.  


        private StreamReader _streamSourceReader;


        private Timer _streamSourceTimer;

        /// <summary>
        /// The sample rate of the source stream.
        /// </summary>
        private AudioSamplingRatesEnum _streamSourceSampleRate;

        public uint RtpPacketsSent
        {
            get { return base.AudioRtcpSession.PacketsSentCount; }
        }

        public uint RtpPacketsReceived
        {
            get { return base.AudioRtcpSession.PacketsReceivedCount; }
        }

        /// <summary>
        /// Fires when an audio sample from the remote party has been decoded into a buffer
        /// of the default 8KHz PCM.
        /// </summary>
        //public event Action<byte[]> OnRemoteAudioSampleReady;

        /// <summary>
        /// Fires when an audio sample from the remote party has been decoded into an
        /// 16KHz PCM buffer. The 8KHz and 16KHz events originate from the same RTP stream
        /// so only one or the other should be handled.
        /// </summary>
        //public event Action<byte[]> OnRemote16KHzPcmSampleReady;

        /// <summary>
        /// Fires when the current send audio from stream operation completes. Send from
        /// stream operations are intended to be short snippets of audio that get sent 
        /// as interruptions to the primary audio stream.
        /// </summary>
        public event Action OnSendFromAudioStreamComplete;

        /// <summary>
        /// Creates an audio only RTP session that can supply an audio stream to the caller.
        /// </summary>
        /// <param name="audioOptions">The options that determine the type of audio to stream to the remote party. Example
        /// type of audio sources are music, silence, white noise etc.</param>
        /// <param name="audioCodecs">The audio codecs to support.</param>
        /// <param name="bindAddress">Optional. If specified this address will be used as the bind address for any RTP
        /// and control sockets created. Generally this address does not need to be set. The default behaviour
        /// is to bind to [::] or 0.0.0.0,d depending on system support, which minimises network routing
        /// causing connection issues.</param>
        /// <param name="bindPort">Optional. If specified the RTP socket will attempt to bind to this port. If the port
        /// is already in use the RTP channel will not be created. Generally the port should be left as 0 which will
        /// result in the Operating System choosing an ephemeral port.</param>
        public MultiSourceAudioSession(
            AudioSourceOptions audioOptions,
            IPlatformMediaSession platformMediaSession,
            IPAddress bindAddress = null,
            int bindPort = 0) :
            base(platformMediaSession, bindAddress, bindPort)
        {
            _audioOpts = audioOptions;
        }

        public override void Close(string reason)
        {
            base.Close(reason);

            _audioStreamTimer?.Dispose();
            _audioStreamReader?.Close();
            StopSendFromAudioStream();
        }

        /// <summary>
        /// Initialises the audio source as required.
        /// </summary>
        public override async Task Start()
        {
            if (!IsStarted)
            {
                await base.Start();

                if (AudioLocalTrack == null || AudioLocalTrack.Capabilities == null || AudioLocalTrack.Capabilities.Count == 0)
                {
                    throw new ApplicationException("Cannot start audio session without a local audio track being available.");
                }
                else if (AudioRemoteTrack == null || AudioRemoteTrack.Capabilities == null || AudioRemoteTrack.Capabilities.Count == 0)
                {
                    throw new ApplicationException("Cannot start audio session without a remote audio track being available.");
                }

                _sendingFormat = base.GetSendingFormat(SDPMediaTypesEnum.audio);
                _sendingAudioSampleRate = SDPMediaFormatInfo.GetClockRate(_sendingFormat.FormatCodec);
                _sendingAudioRtpRate = SDPMediaFormatInfo.GetRtpClockRate(_sendingFormat.FormatCodec);

                Log.LogDebug($"RTP audio session selected sending codec {_sendingFormat.FormatCodec}.");

                SetSource(_audioOpts);
            }
        }

        /// <summary>
        /// Same as the async method of the same name but returns a task that waits for the 
        /// stream send to complete.
        /// </summary>
        /// <param name="audioStream">The stream containing the 16 bit PCM sampled at either 8 or 16 Khz 
        /// to send to the remote party.</param>
        /// <param name="streamSampleRate">The sample rate of the supplied PCM samples. Supported rates are
        /// 8 or 16 KHz.</param>
        /// <returns>A task that completes once the stream has been fully sent.</returns>
        public async Task SendAudioFromStream(Stream audioStream, AudioSamplingRatesEnum streamSampleRate)
        {
            if (audioStream != null && audioStream.Length > 0)
            {
                // Stop any existing send from stream operation.
                StopSendFromAudioStream();

                TaskCompletionSource<bool> tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                Action handler = null;
                handler = () =>
                {
                    tcs.TrySetResult(true);
                    OnSendFromAudioStreamComplete -= handler;
                };
                OnSendFromAudioStreamComplete += handler;

                InitialiseSendAudioFromStreamTimer(audioStream, streamSampleRate);

                _streamSourceTimer.Change(AUDIO_SAMPLE_PERIOD_MILLISECONDS, AUDIO_SAMPLE_PERIOD_MILLISECONDS);

                await tcs.Task;
            }
        }

        /// <summary>
        /// Cancels an in-progress send audio from stream operation.
        /// </summary>
        public void CancelSendAudioFromStream()
        {
            StopSendFromAudioStream();
        }

        /// <summary>
        /// Sets the source for the session. Overrides any existing source.
        /// </summary>
        /// <param name="sourceOptions">The new audio source.</param>
        public void SetSource(AudioSourceOptions sourceOptions)
        {
            // If required start the audio source.
            if (sourceOptions != null)
            {
                if (sourceOptions.AudioSource == AudioSourcesEnum.None)
                {
                    _audioStreamTimer?.Dispose();
                    _audioStreamReader?.Close();
                    StopSendFromAudioStream();
                }
                else if (sourceOptions.AudioSource == AudioSourcesEnum.Silence)
                {
                    _audioStreamTimer = new Timer(SendSilenceSample, null, 0, AUDIO_SAMPLE_PERIOD_MILLISECONDS);
                }
                else if (sourceOptions.AudioSource == AudioSourcesEnum.PinkNoise ||
                     sourceOptions.AudioSource == AudioSourcesEnum.WhiteNoise ||
                    sourceOptions.AudioSource == AudioSourcesEnum.SineWave)
                {
                    _signalGenerator = new SignalGenerator(_sendingAudioSampleRate, 1);

                    switch (sourceOptions.AudioSource)
                    {
                        case AudioSourcesEnum.PinkNoise:
                            _signalGenerator.Type = SignalGeneratorType.Pink;
                            break;
                        case AudioSourcesEnum.SineWave:
                            _signalGenerator.Type = SignalGeneratorType.Sin;
                            break;
                        case AudioSourcesEnum.WhiteNoise:
                        default:
                            _signalGenerator.Type = SignalGeneratorType.White;
                            break;
                    }

                    _audioStreamTimer = new Timer(SendSignalGeneratorSample, null, 0, AUDIO_SAMPLE_PERIOD_MILLISECONDS);
                }
                else if (sourceOptions.AudioSource == AudioSourcesEnum.Music)
                {
                    if (sourceOptions.SourceFiles == null || !sourceOptions.SourceFiles.ContainsKey(_sendingFormat.FormatCodec))
                    {
                        Log.LogWarning($"Source file not set for codec {_sendingFormat.FormatCodec}.");
                    }
                    else
                    {
                        string sourceFile = sourceOptions.SourceFiles[_sendingFormat.FormatCodec];

                        if (String.IsNullOrEmpty(sourceFile) || !File.Exists(sourceFile))
                        {
                            Log.LogWarning("Could not start audio music source as the source file does not exist.");
                        }
                        else
                        {
                            _audioStreamReader = new StreamReader(sourceFile);
                            _audioStreamTimer = new Timer(SendMusicSample, null, 0, AUDIO_SAMPLE_PERIOD_MILLISECONDS);
                        }
                    }
                }
            }

            _audioOpts = sourceOptions;
        }


        /// <summary>
        /// Sends a stream containing 16 bit PCM audio to the remote party. Calling this method
        /// will pause the existing audio source until the stream has been sent.
        /// </summary>
        /// <param name="audioStream">The stream containing the 16 bit PCM, sampled at either 8 or 16 Khz,
        /// to send to the remote party.</param>
        /// <param name="streamSampleRate">The sample rate of the supplied PCM samples. Supported rates are
        /// 8 or 16 KHz.</param>
        private void InitialiseSendAudioFromStreamTimer(Stream audioStream, AudioSamplingRatesEnum streamSampleRate)
        {
            if (audioStream != null && audioStream.Length > 0)
            {
                Log.LogDebug($"Sending audio stream length {audioStream.Length}.");

                _streamSendInProgress = true;

                _streamSourceSampleRate = streamSampleRate;
                _streamSourceReader = new StreamReader(audioStream);
                _streamSourceTimer = new Timer(SendStreamSample, null, Timeout.Infinite, Timeout.Infinite);
            }
        }

        /// <summary>
        /// Sends audio samples read from a file.
        /// </summary>
        private void SendMusicSample(object state)
        {
            if (!_streamSendInProgress)
            {
                lock (_audioStreamTimer)
                {
                    int sampleRate = SDPMediaFormatInfo.GetRtpClockRate(_sendingFormat.FormatCodec);
                    int sampleSize = sampleRate / 1000 * AUDIO_SAMPLE_PERIOD_MILLISECONDS;
                    byte[] sample = new byte[sampleSize];

                    int bytesRead = _audioStreamReader.BaseStream.Read(sample, 0, sample.Length);

                    if (bytesRead > 0)
                    {
                        SendAudioFrame((uint)sampleSize, (int)_sendingFormat.FormatCodec, sample);
                    }

                    if (bytesRead == 0 || _audioStreamReader.EndOfStream)
                    {
                        _audioStreamReader.BaseStream.Position = 0;
                    }
                }
            }
        }

        /// <summary>
        /// Sends the sounds of silence.
        /// </summary>
        private void SendSilenceSample(object state)
        {
            if (!_streamSendInProgress)
            {
                lock (_audioStreamTimer)
                {
                    int outputBufferSize = _sendingAudioRtpRate / 1000 * AUDIO_SAMPLE_PERIOD_MILLISECONDS;

                    if (_sendingFormat.FormatCodec == SDPMediaFormatsEnum.G722)
                    {
                        int inputBufferSize = _sendingAudioSampleRate / 1000 * AUDIO_SAMPLE_PERIOD_MILLISECONDS;
                        short[] silencePcm = new short[inputBufferSize];

                        RawAudioSampleReady(AUDIO_SAMPLE_PERIOD_MILLISECONDS, silencePcm, AudioSamplingRatesEnum.Rate8KHz);
                    }
                    else if (_sendingFormat.FormatCodec == SDPMediaFormatsEnum.PCMU
                            || _sendingFormat.FormatCodec == SDPMediaFormatsEnum.PCMA)
                    {
                        if (_silenceBuffer == null || _silenceBuffer.Length != outputBufferSize)
                        {
                            _silenceBuffer = new byte[outputBufferSize];
                            SetSilenceBuffer(_silenceBuffer, 0);
                        }

                        // No encoding required for PCMU/PCMA silence.
                        SendAudioFrame((uint)outputBufferSize, (int)_sendingFormat.FormatCodec, _silenceBuffer);
                    }
                    else
                    {
                        Log.LogWarning($"SendSilenceSample does not know how to encode {_sendingFormat.FormatCodec}.");
                    }
                }
            }
        }

        /// <summary>
        /// Sends a sample from a signal generator generated waveform.
        /// </summary>
        private void SendSignalGeneratorSample(object state)
        {
            if (!_streamSendInProgress)
            {
                lock (_audioStreamTimer)
                {
                    int inputBufferSize = _sendingAudioSampleRate / 1000 * AUDIO_SAMPLE_PERIOD_MILLISECONDS;
                    int outputBufferSize = _sendingAudioRtpRate / 1000 * AUDIO_SAMPLE_PERIOD_MILLISECONDS;

                    // Get the signal generator to generate the samples and then convert from
                    // signed linear to PCM.
                    float[] linear = new float[inputBufferSize];
                    _signalGenerator.Read(linear, 0, inputBufferSize);
                    short[] pcm = linear.Select(x => (short)(x * LINEAR_MAXIMUM)).ToArray();

                    RawAudioSampleReady(AUDIO_SAMPLE_PERIOD_MILLISECONDS, pcm, AudioSamplingRatesEnum.Rate8KHz);
                }
            }
        }

        /// <summary>
        /// Sends audio samples read from a file containing 16 bit PCM samples.
        /// </summary>
        private void SendStreamSample(object state)
        {
            lock (_streamSourceTimer)
            {
                if (_streamSourceReader?.BaseStream?.CanRead == true)
                {
                    int sampleRate = (_streamSourceSampleRate == AudioSamplingRatesEnum.Rate8KHz) ? 8000 : 16000;
                    int sampleSize = sampleRate * 2 / 1000 * AUDIO_SAMPLE_PERIOD_MILLISECONDS;
                    byte[] sample = new byte[sampleSize];

                    int bytesRead = _streamSourceReader.BaseStream.Read(sample, 0, sample.Length);

                    if (bytesRead > 0)
                    {
                        //Log.LogDebug($"Audio stream reader bytes read {bytesRead}, position {_audioPcmStreamReader.BaseStream.Position}, length {_audioPcmStreamReader.BaseStream.Length}.");

                        if (bytesRead < sample.Length)
                        {
                            // If the sending codec supports it fill up any short samples with silence.
                            if (_sendingFormat.FormatCodec == SDPMediaFormatsEnum.PCMA ||
                                _sendingFormat.FormatCodec == SDPMediaFormatsEnum.PCMU)
                            {
                                SetSilenceBuffer(sample, bytesRead);
                            }
                        }

                        RawAudioSampleReady(AUDIO_SAMPLE_PERIOD_MILLISECONDS, sample, _streamSourceSampleRate);

                        if (_streamSourceReader.EndOfStream || _streamSourceReader.BaseStream.Position >= _streamSourceReader.BaseStream.Length)
                        {
                            Log.LogDebug("Send audio from stream completed.");
                            StopSendFromAudioStream();
                        }
                    }
                    else
                    {
                        Log.LogWarning("Failed to read from audio stream source.");
                        StopSendFromAudioStream();
                    }
                }
                else
                {
                    Log.LogWarning("Failed to read from audio stream source, stream null or closed.");
                    StopSendFromAudioStream();
                }
            }
        }

        /// <summary>
        /// Stops a send from audio stream job.
        /// </summary>
        private void StopSendFromAudioStream()
        {
            _streamSourceReader?.Close();
            _streamSourceTimer?.Dispose();
            _streamSendInProgress = false;

            OnSendFromAudioStreamComplete?.Invoke();
        }

        /// <summary>
        /// Fills up the silence buffer for the sending format and period.
        /// </summary>
        /// <param name="length">The required length for the silence buffer.</param>
        private void SetSilenceBuffer(byte[] buffer, int startPosn)
        {
            for (int index = startPosn; index < buffer.Length - 1; index += 2)
            {
                if (_sendingFormat.FormatCodec == SDPMediaFormatsEnum.PCMA)
                {
                    buffer[index] = PCMA_SILENCE_BYTE_ZERO;
                    buffer[index + 1] = PCMA_SILENCE_BYTE_ONE;
                }
                else if (_sendingFormat.FormatCodec == SDPMediaFormatsEnum.PCMU)
                {
                    buffer[index] = PCMU_SILENCE_BYTE_ZERO;
                    buffer[index + 1] = PCMU_SILENCE_BYTE_ONE;
                }
            }
        }
    }
}