# Reference token IDs

`tokenizer-reference.json` contains 33 original test texts and their token IDs.
Expected IDs were computed with tiktoken 0.14.0 (`encode_ordinary`, NFC), using
the ordinary Qwen byte ranks in the user-installed YuE2 GGUF, revision
`64b030e3deb6e8150d2b7c0db641ef5a17eca8a3`.
It contains no model weights or vocabulary tables.

Reference protocol and pre-tokenization pattern:
https://github.com/ServeurpersoCom/yue2.cpp/blob/master/tests/test-bpe.py
https://github.com/ServeurpersoCom/yue2.cpp/blob/master/src/prompt.h

Set `LOPATA_MUSIC_TOKENIZER_PATH` to an already installed GGUF to run the parity
test. Without it that integration test is explicitly inconclusive; ordinary
budget/editor tests need neither weights nor an inference runtime.
tiktoken is a temporary developer validation tool, not an application dependency.
