# Context maintenance

The generated macOS source map lives under the inherited `.claude/context/linux/` path.

```bash
bash .claude/context/gen-context-linux.sh
bash .claude/context/gen-context-linux.sh --check
```

Run the structural adapter from PowerShell:

```powershell
powershell -File .claude/context/manage-context.ps1 summary
powershell -File .claude/context/manage-context.ps1 check
powershell -File .claude/context/manage-context.ps1 deep-check
```

Regenerate after adding, removing, or moving projects/source files or changing public declarations.
Do not hand-edit generated masters, index shards, symbol shards, or `deps.json`.
