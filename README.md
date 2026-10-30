# nexora-installer — Nexora Setup
Installateur personnalisable Avalonia 11.2.3, dans `Nexora.Installer/`.

**Dépendances locales** : dossiers frères `nexora`, `nexora-fido`, `nexora-usb` et `nexora-crypt`. L'installateur embarque le résultat publié de `nexora` et utilise la version du projet de l'application lors de l'export.

```powershell
.\publish.ps1 -MakeInstaller
```

Génère `../nexora/dist/Nexora-Setup.exe` si la compilation Windows réussit. Ne distribue aucun pilote USB/IP.

L'assistant vérifie l'intégrité SHA-256 de tous les fichiers, les chemins, les droits d'écriture, les fichiers verrouillés, l'espace nécessaire sur chaque volume et l'instance de Nexora à mettre à jour. USB/IP est requis par défaut pour connecter les clés ; l'utilisateur peut choisir explicitement de le configurer plus tard. Le client est recherché au même emplacement que dans Nexora : `Program Files/USBip/usbip.exe`.

Pour produire un export séparé sans remplacer le dossier dist :

```powershell
.\publish.ps1 -MakeInstaller -DistDirectory E:\Exports\Nexora
```

Tests Windows isolés (aucune modification de registre, raccourci ou pilote) :

```powershell
dotnet run --project Nexora.Installer.Tests -c Release
```

Le Setup exporté propose aussi `--verify-package` : il vérifie uniquement son archive embarquée et retourne 0 en cas de succès, 1 sinon. Ce mode n'installe pas l'application.

La configuration USB/IP, l'intégration Windows complète et les scénarios de redémarrage nécessitent encore des essais dans une VM dédiée. Les SHA-256 vérifient l'intégrité du paquet ; la signature Authenticode du Setup reste une amélioration distincte à mettre en place avant distribution publique.
