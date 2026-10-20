# nexora-installer — Nexora 1.1
Installateur personnalisable Avalonia 11.2.3, dans `Nexora.Installer/`.

**Dépendances locales** : dossiers frères `nexora`, `nexora-fido`, `nexora-usb`. L'installateur embarque le résultat publié de `nexora`.

```powershell
.\publish.ps1 -MakeInstaller
```

Génère `../nexora/dist/Nexora-Setup.exe` si la compilation Windows réussit. Ne distribue aucun pilote USB/IP.
