# Third-Party Notices

wsnap is licensed under **GPL-3.0-only** (see `LICENSE`). The distributed `wsnap.exe`
bundles the third-party components listed below. Each is used under its own license, all of
which are compatible with GPL-3.0. This file is provided to satisfy the attribution and
license-inclusion requirements of those licenses (Apache-2.0 §4, the MIT/BSD copyright
notice clauses, and the Boost Software License).

Apache-2.0 is one-way compatible with GPLv3: the combined work is distributed under
GPL-3.0-only, while each component below retains its original license.

---

## OCR engine and models

### RapidOcrNet — Apache-2.0
- Source: https://github.com/BobLd/RapidOcrNet
- Copyright © BobLd. Adapted from RapidAI / RapidOCR (https://github.com/RapidAI/RapidOCR).
- License: Apache License 2.0 — https://www.apache.org/licenses/LICENSE-2.0

### PaddleOCR PP-OCRv5 models — Apache-2.0
- Detection (`ch_PP-OCRv5_mobile_det`), angle classification (`ch_ppocr_mobile_v2.0_cls`),
  and recognition models are PaddleOCR PP-OCRv5 models.
- Source: https://github.com/PaddlePaddle/PaddleOCR — Copyright © PaddlePaddle Authors.
- License: Apache License 2.0.

### Korean recognition model + dictionary — Apache-2.0
- ONNX-converted Korean PP-OCRv5 recognition model (`korean_rec.onnx`) and its dictionary
  (`korean_dict.txt`), from https://huggingface.co/monkt/paddleocr-onnx (license: apache-2.0),
  derived from https://huggingface.co/PaddlePaddle/korean_PP-OCRv5_mobile_rec (apache-2.0).
- License: Apache License 2.0.

---

## Runtime libraries (bundled in the single-file exe)

### Microsoft.ML.OnnxRuntime / Microsoft.ML.OnnxRuntime.Managed — MIT
- Source: https://github.com/microsoft/onnxruntime — Copyright © Microsoft Corporation.
- License: MIT.

### SkiaSharp (and SkiaSharp.NativeAssets.*) — MIT
- Source: https://github.com/mono/SkiaSharp — Copyright © Microsoft Corporation.
- License: MIT. The bundled native `libSkiaSharp` embeds Google's Skia
  (https://skia.org), Copyright © Google LLC, under the BSD-3-Clause license, along with
  Skia's own third-party components under their respective permissive licenses.

### Clipper2 — Boost Software License 1.0
- Source: https://github.com/AngusJohnson/Clipper2 — Copyright © Angus Johnson.
- License: Boost Software License 1.0 — https://www.boost.org/LICENSE_1_0.txt

### System.Numerics.Tensors — MIT
- Part of the .NET libraries — Copyright © .NET Foundation and Contributors.
- License: MIT.

---

## .NET runtime

The self-contained build bundles the .NET 8 runtime, Copyright © .NET Foundation and
Contributors, licensed under the MIT License (https://github.com/dotnet/runtime).

---

### Full license texts

The Apache-2.0, MIT, BSD-3-Clause, and Boost Software License texts are available at the URLs
above. The Apache-2.0 text is also reproduced at https://www.apache.org/licenses/LICENSE-2.0.
No NOTICE-file content was required to be propagated from the above components beyond the
attributions listed here at the time of bundling.
