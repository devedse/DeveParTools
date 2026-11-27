# Third-party notices

## Leopard-RS
The FFT Reed-Solomon code in `DeveParTools.Core/Codes/Fft` is a C# port of
[Leopard-RS](https://github.com/catid/leopard) by Christopher A. Taylor, BSD 3-Clause License:

> Copyright (c) 2017 Christopher A. Taylor. All rights reserved.
> Redistribution and use in source and binary forms, with or without modification, are permitted provided that
> the following conditions are met: redistributions of source code must retain the above copyright notice, this list
> of conditions and the following disclaimer; redistributions in binary form must reproduce the above copyright
> notice, this list of conditions and the following disclaimer in the documentation and/or other materials provided
> with the distribution; neither the name of Leopard-RS nor the names of its contributors may be used to endorse or
> promote products derived from this software without specific prior written permission.
> THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES
> ARE DISCLAIMED.

## Fluent UI System Icons
Icons in `DeveParTools.App/Themes/Icons.axaml` are from
[Fluent UI System Icons](https://github.com/microsoft/fluentui-system-icons), Copyright (c) 2020 Microsoft
Corporation, MIT License: see [LICENSES/FluentUI-System-Icons-MIT.txt](LICENSES/FluentUI-System-Icons-MIT.txt).

## Reference implementations (tests only)
par2cmdline (GPL-2.0), par2cmdline-turbo (GPL-2.0) and par3cmdline (LGPL-2.1) are used only as external test
oracles in `DeveParTools.InteropTests`; none of their code is included.

## BLAKE3 test vectors (tests only)
`DeveParTools.Core.Tests/TestData/blake3_vectors.txt` holds the official test vectors from
[BLAKE3](https://github.com/BLAKE3-team/BLAKE3) (`test_vectors/test_vectors.json`), released under CC0-1.0 /
Apache-2.0. The BLAKE3 implementation itself is written from the specification.

## Intel Software Development Emulator (CI only)
CI runs the tests under [Intel SDE](https://www.intel.com/software/sde) to emulate other CPUs. It is downloaded
during the build and is not part of this repository or of the releases.
