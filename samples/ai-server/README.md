

Install Qwen:

```sh
curl -fsSL https://ollama.com/install.sh | sh
ollama pull qwen2:0.5b
```

Incoming `/chat/1.0.0` prompts may contain up to 1 MiB (1,048,576 bytes) of UTF-8
payload, including the trailing newline. The console and MAUI chat clients use
the same limit for incoming AI replies, independently of the 1,024-byte
multistream negotiation limit.
