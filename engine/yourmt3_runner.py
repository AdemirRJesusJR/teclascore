"""Roda o YourMT3+ numa gravação e grava as notas reconhecidas em JSON. Este arquivo é executado pelo Python do
componente YourMT3+ (com PyTorch), não pelo Python do TeclaScore: quem o chama é yourmt3_engine.recognize.

Segue o app.py e o model_helper.py dos autores (modelo "YPTF.MoE+Multi (noPS)"), com duas diferenças: o áudio é lido
com soundfile (o WAV que o TeclaScore já decodificou) e a inferência vai em blocos, para informar o andamento.
As linhas que começam com "@" na saída são mensagens de progresso.

Uso: python yourmt3_runner.py --codigo PASTA_DO_YOURMT3 --audio gravacao.wav --saida notas.json [--cpu]"""
import argparse, json, os, sys


def say(message): print('@' + message, flush=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--codigo', required=True); parser.add_argument('--audio', required=True)
    parser.add_argument('--saida', required=True); parser.add_argument('--cpu', action='store_true')
    options = parser.parse_args()
    audio_path, output_path = os.path.abspath(options.audio), os.path.abspath(options.saida)
    code = os.path.abspath(options.codigo)
    os.chdir(code)                                       # o código dos autores procura amt/logs a partir daqui
    sys.path[:0] = [os.path.join(code, 'amt', 'src'), code]
    os.environ.setdefault('WANDB_MODE', 'disabled')
    say('Carregando o modelo YourMT3+…')
    import numpy as np, soundfile, torch, torchaudio
    from collections import Counter
    from model_helper import load_model_checkpoint
    from utils.audio import slice_padded_array
    from utils.note2event import mix_notes
    from utils.event2note import merge_zipped_note_events_and_ties_to_notes
    device = 'cuda' if torch.cuda.is_available() and not options.cpu else 'cpu'
    checkpoint = 'mc13_256_g4_all_v7_mt3f_sqr_rms_moe_wf4_n8k2_silu_rope_rp_b36_nops@last.ckpt'
    arguments = [checkpoint, '-p', '2024', '-tk', 'mc13_full_plus_256', '-dec', 'multi-t5', '-nl', '26', '-enc', 'perceiver-tf',
                 '-sqr', '1', '-ff', 'moe', '-wf', '4', '-nmoe', '8', '-kmoe', '2', '-act', 'silu', '-epe', 'rope', '-rp', '1',
                 '-ac', 'spec', '-hop', '300', '-atc', '1', '-pr', '16' if device == 'cuda' else '32']
    model = load_model_checkpoint(args=arguments, device='cpu').to(device)
    samples, rate = soundfile.read(audio_path, dtype='float32', always_2d=True)
    audio = torch.from_numpy(samples.mean(axis=1)).unsqueeze(0)
    audio = torchaudio.functional.resample(audio, rate, model.audio_cfg['sample_rate'])
    frames = model.audio_cfg['input_frames']
    segments = slice_padded_array(audio, frames, frames)
    segments = torch.from_numpy(segments.astype('float32')).to(device).unsqueeze(1)          # (trechos, 1, amostras)
    total = segments.shape[0]; batch = 8 if device == 'cuda' else 2
    tokens = []
    with torch.inference_mode():
        for start in range(0, total, batch):
            say(f'Reconhecendo as notas com o YourMT3+… {round(100 * start / total)}%')
            predicted, _ = model.inference_file(bsz=batch, audio_segments=segments[start:start + batch])
            tokens.extend(predicted)
    starts = [frames * i / model.audio_cfg['sample_rate'] for i in range(total)]
    channels = model.task_manager.num_decoding_channels
    by_channel = []; errors = Counter()
    for channel in range(channels):
        arrays = [array[:, channel, :] for array in tokens]
        zipped, _, _ = model.task_manager.detokenize_list_batches(arrays, starts, return_events=True)
        notes, count = merge_zipped_note_events_and_ties_to_notes(zipped)
        by_channel.append(notes); errors += count
    notes = mix_notes(by_channel)
    result = {'model': 'YPTF.MoE+Multi (noPS)', 'device': device, 'seconds': samples.shape[0] / rate,
              'notes': [{'start': float(n.onset), 'end': float(n.offset), 'pitch': int(n.pitch), 'program': int(n.program),
                         'drum': bool(n.is_drum), 'velocity': int(n.velocity)} for n in notes if n.offset > n.onset]}
    with open(output_path, 'w', encoding='utf-8') as stream: json.dump(result, stream)
    say(f'{len(result["notes"])} notas reconhecidas.')


if __name__ == '__main__':
    main()
