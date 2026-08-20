# Third-party notices - Toki for Windows

Toki runs entirely on your device using the open-source models and libraries below.
This file, and the in-app **Settings → Built with** screen, satisfy the attribution
terms of their licences. Each entry links to the upstream source, where the full,
authoritative licence text lives. Full licence copies are distributed alongside the
application binary.

Toki itself is not open source; the components below are, under their own licences.

---

## Models

### Parakeet TDT 0.6b v2 - speech-to-text
- **Author:** NVIDIA
- **Licence:** Creative Commons Attribution 4.0 International (CC-BY-4.0)
- **Source:** https://huggingface.co/nvidia/parakeet-tdt-0.6b-v2
- **Licence text:** https://creativecommons.org/licenses/by/4.0/legalcode
- **Modifications:** the model is redistributed quantised to int8 (ONNX), as packaged
  by the sherpa-onnx project. No other changes.

> CC-BY-4.0 requires attribution to NVIDIA, a link to the licence, and an indication
> that the material was modified. All three are provided here and on the in-app
> "Built with" screen.

### Qwen3-4B-Instruct-2507 - on-device notes & chat
- **Author:** Alibaba Cloud (Qwen team)
- **Licence:** Apache License 2.0
- **Source:** https://huggingface.co/Qwen/Qwen3-4B-Instruct-2507
- **Redistribution:** Q4_K_M GGUF quantisation (community build).

### bge-small-en-v1.5 - meaning search
- **Author:** Beijing Academy of Artificial Intelligence (BAAI)
- **Licence:** MIT
- **Source:** https://huggingface.co/BAAI/bge-small-en-v1.5
- **Redistribution:** Q8_0 GGUF quantisation (community build).

### Silero VAD v5 - voice activity detection
- **Author:** Silero Team
- **Licence:** MIT
- **Source:** https://github.com/snakers4/silero-vad
- **Redistribution:** ONNX export, as packaged by the sherpa-onnx project.

### pyannote segmentation-3.0 - speaker segmentation
- **Author:** pyannote (Hervé Bredin), (c) 2022 CNRS
- **Licence:** MIT
- **Source:** https://huggingface.co/pyannote/segmentation-3.0
- **Redistribution:** ONNX export, as packaged by the sherpa-onnx project.

### 3D-Speaker CAM++ - speaker embedding
- **Author:** Alibaba (3D-Speaker / ModelScope team)
- **Licence:** Apache License 2.0 (code and weights)
- **Source:** https://github.com/modelscope/3D-Speaker
- **Redistribution:** `3dspeaker_speech_campplus_sv_en_voxceleb_16k.onnx`, as packaged
  by the sherpa-onnx project.

---

## Engines & libraries

| Component | Author | Licence | Source |
|---|---|---|---|
| sherpa-onnx | k2-fsa | Apache-2.0 | https://github.com/k2-fsa/sherpa-onnx |
| ONNX Runtime | Microsoft | MIT | https://github.com/microsoft/onnxruntime |
| LLamaSharp | SciSharp | MIT | https://github.com/SciSharp/LLamaSharp |
| llama.cpp | Georgi Gerganov & contributors | MIT | https://github.com/ggerganov/llama.cpp |
| NAudio | Mark Heath & contributors | MIT | https://github.com/naudio/NAudio |
| Microsoft.Data.Sqlite | Microsoft (.NET) | MIT | https://github.com/dotnet/efcore |
| SQLite (engine) | D. Richard Hipp & contributors | Public Domain | https://www.sqlite.org/copyright.html |
| H.NotifyIcon.Wpf | Havendv | MIT | https://github.com/HavenDV/H.NotifyIcon |
| SharpCompress | Adam Hathcock | MIT | https://github.com/adamhathcock/sharpcompress |
| Microsoft.Recognizers.Text | Microsoft | MIT | https://github.com/microsoft/Recognizers-Text |
| System.Security.Cryptography.ProtectedData | Microsoft (.NET) | MIT | https://github.com/dotnet/runtime |

Fonts (Segoe UI Variable, Segoe Fluent Icons) are provided by Windows and are not
redistributed by Toki.

---

*Whisper (whisper.cpp) is referenced in the codebase only as a deferred error-fallback
and is **not** shipped in the current build; it is intentionally omitted from these
notices until it is actually bundled.*
