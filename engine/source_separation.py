"""Separação da gravação em voz, baixo, bateria e demais instrumentos, como fazem os transcritores comerciais antes de
escrever a partitura.

Usa o HTDemucs v4 (Meta, licença MIT; engine/models/htdemucs/htdemucs.onnx, exportado com a STFT fora do grafo) com o
onnxruntime. A STFT, a iSTFT e a soma dos trechos sobrepostos seguem o código original do Demucs (demucs/htdemucs.py
_spec/_ispec e demucs/apply.py apply_model): Hann periódica de 4096 pontos, salto de 1024, STFT normalizada, trechos de
7,8 s com 25% de sobreposição somados com peso triangular. A soma das quatro pistas reconstrói a mixagem."""
from pathlib import Path
import math
import numpy as np

MODEL = Path(__file__).parent / 'models' / 'htdemucs' / 'htdemucs.onnx'
SAMPLE_RATE = 44100
N_FFT = 4096
HOP = 1024
SEGMENT = 343980                       # 7,8 s
SOURCES = ('drums', 'bass', 'other', 'vocals')
OVERLAP = .25
_WINDOW = (0.5 - 0.5 * np.cos(2 * np.pi * np.arange(N_FFT) / N_FFT)).astype(np.float64)   # Hann periódica


def available():
    """O modelo de separação está instalado e o onnxruntime pode carregá-lo."""
    if not MODEL.exists(): return False
    try: import onnxruntime  # noqa: F401
    except ImportError: return False
    return True


def _stft(x):
    """torch.stft(n_fft=4096, hop=1024, janela de Hann, normalized=True, center=True, reflect) de (canais, amostras)."""
    pad = N_FFT // 2
    x = np.pad(x, ((0, 0), (pad, pad)), mode='reflect')
    count = 1 + (x.shape[-1] - N_FFT) // HOP
    frames = np.lib.stride_tricks.as_strided(x, (x.shape[0], count, N_FFT), (x.strides[0], x.strides[1] * HOP, x.strides[1]))
    return (np.fft.rfft(frames * _WINDOW, axis=-1) / math.sqrt(N_FFT)).transpose(0, 2, 1)   # (canais, 2049, quadros)


def _istft(z, length):
    """torch.istft inversa da _stft acima, com o comprimento final pedido."""
    frames = np.fft.irfft(z.transpose(0, 2, 1), n=N_FFT, axis=-1) * math.sqrt(N_FFT) * _WINDOW
    count = frames.shape[1]
    total = N_FFT + HOP * (count - 1)
    out = np.zeros((z.shape[0], total)); norm = np.zeros(total)
    for i in range(count):
        out[:, i * HOP:i * HOP + N_FFT] += frames[:, i]; norm[i * HOP:i * HOP + N_FFT] += _WINDOW ** 2
    out /= np.where(norm > 1e-11, norm, 1)
    start = N_FFT // 2
    return out[:, start:start + length]


def _spec(x):
    """HTDemucs._spec: espectrograma com o enchimento do Demucs, quadros alinhados ao salto."""
    length = x.shape[-1]
    frames = int(math.ceil(length / HOP))
    pad = HOP // 2 * 3
    x = np.pad(x, ((0, 0), (pad, pad + frames * HOP - length)), mode='reflect')
    return _stft(x)[:, :-1, 2:2 + frames]


def _ispec(z, length):
    """HTDemucs._ispec."""
    z = np.pad(z, ((0, 0), (0, 1), (2, 2)))
    pad = HOP // 2 * 3
    total = HOP * int(math.ceil(length / HOP)) + 2 * pad
    return _istft(z, total)[:, pad:pad + length]


def _run_segment(session, segment):
    """Uma janela de 7,8 s (2, SEGMENT) → as quatro pistas (4, 2, SEGMENT)."""
    z = _spec(segment.astype(np.float64))                               # (2, 2048, 336) complexo
    mag = np.stack([z[0].real, z[0].imag, z[1].real, z[1].imag])[None].astype(np.float32)
    freq, time = session.run(['freq', 'time'], {'mix': segment[None].astype(np.float32), 'mag': mag})
    freq, time = freq[0].astype(np.float64), time[0].astype(np.float64)   # (4, 4, 2048, 336), (4, 2, SEGMENT)
    stems = np.empty((len(SOURCES), 2, SEGMENT))
    for s in range(len(SOURCES)):
        spectrum = np.stack([freq[s, 0] + 1j * freq[s, 1], freq[s, 2] + 1j * freq[s, 3]])
        stems[s] = _ispec(spectrum, SEGMENT) + time[s]
    return stems


def to_stereo_44k(audio, rate):
    """(amostras,) ou (amostras, canais) em qualquer taxa → (2, amostras) a 44,1 kHz."""
    import audio_transcription as at
    audio = np.asarray(audio, np.float32)
    if audio.ndim == 1: audio = audio[:, None]
    if audio.shape[1] == 1: audio = np.repeat(audio, 2, axis=1)
    audio = audio[:, :2]
    if rate != SAMPLE_RATE: audio = np.stack([at.resample(audio[:, c], rate, SAMPLE_RATE) for c in range(2)], axis=1)
    return audio.T.copy()


def separate(audio, progress=lambda done, total: None, session=None):
    """(2, amostras) a 44,1 kHz → dict com 'drums', 'bass', 'other', 'vocals', cada um (2, amostras)."""
    import onnxruntime
    if session is None:
        options = onnxruntime.SessionOptions()
        options.graph_optimization_level = onnxruntime.GraphOptimizationLevel.ORT_ENABLE_ALL
        session = onnxruntime.InferenceSession(str(MODEL), options, providers=['CPUExecutionProvider'])
    audio = np.asarray(audio, np.float64)
    length = audio.shape[-1]
    # separate.py: a mixagem entra com média zero e desvio 1 e volta à escala original no fim.
    reference = audio.mean(0); mean, std = float(reference.mean()), float(reference.std()) or 1.0
    audio = (audio - mean) / std
    stride = int((1 - OVERLAP) * SEGMENT)
    weight = np.concatenate([np.arange(1, SEGMENT // 2 + 1), np.arange(SEGMENT - SEGMENT // 2, 0, -1)]).astype(np.float64)
    weight /= weight.max()
    out = np.zeros((len(SOURCES), 2, length)); total_weight = np.zeros(length)
    offsets = list(range(0, length, stride))
    for index, offset in enumerate(offsets):
        chunk = audio[:, offset:offset + SEGMENT]
        valid = chunk.shape[-1]
        segment = np.zeros((2, SEGMENT)); segment[:, :valid] = chunk
        stems = _run_segment(session, segment)[:, :, :valid]
        out[:, :, offset:offset + valid] += weight[:valid] * stems
        total_weight[offset:offset + valid] += weight[:valid]
        progress(index + 1, len(offsets))
    out = out / total_weight * std + mean
    return {name: out[i].astype(np.float32) for i, name in enumerate(SOURCES)}
