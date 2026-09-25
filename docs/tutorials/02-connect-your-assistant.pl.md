# 2. Podłącz swojego asystenta

**Sytuacja:** CopilotScope działa. **Wynik:** Twoje własne sesje, oceniane w trakcie pracy, z
opóźnieniami i decyzjami o zmianach, których transkrypt na dysku nie zapisuje.

Jedna komenda na asystenta. Każda zapisuje plik ustawień, który ten asystent
faktycznie czyta, więc konfiguracja przeżywa nowe terminale, nowe projekty i
restarty.

```bash
copilotscope connect claude-code     # ~/.claude/settings.json
copilotscope connect vscode          # ustawienia użytkownika VS Code
copilotscope connect copilot-cli     # rc powłoki lub zakres User na Windows
copilotscope connect cowork          # wypisuje, co wpisać w aplikacji
copilotscope connect all             # Claude Code i VS Code razem
```

`copilotscope setup` robi to samo dla każdego asystenta, którego znajdzie na maszynie, pokazując
każdą zmianę i pytając, zanim ją wprowadzi.

Dwie flagi działają wszędzie:

- `--print` pokazuje dokładnie, co zostałoby zapisane, i niczego nie zmienia.
  Uruchom to najpierw, jeśli wolisz zobaczyć, zanim się stanie.
- `--capture` eksportuje także tekst promptów i odpowiedzi. **Domyślnie
  wyłączone**; to przełącznik, który zamienia wdrożenie trzymające same
  metadane we wdrożenie trzymające kod źródłowy i wszystko, co programista
  wkleił do czatu.

`copilotscope disconnect <cel>` usuwa dokładnie te klucze, które zostały dodane,
i zostawia resztę ustawień w spokoju.

## Potem krok, którego żaden skrypt nie wykona

| Asystent | Co musisz zrobić sam |
|---|---|
| VS Code | Przeładuj okno — `Ctrl/Cmd+Shift+P` → *Developer: Reload Window*. Ustawienia są czytane przy starcie rozszerzenia |
| Claude Code | Nic. Uruchom lub zrestartuj `claude` |
| Copilot CLI | Otwórz nowy terminal albo zrób `source` na swoim rc |
| Cowork | Zrestartuj Claude Desktop — konfiguracja jest czytana przy starcie sesji |

Potem **rozmawiaj w trybie agenta**. Same podpowiedzi inline nie produkują
telemetrii czatu i jest to drugi najczęstszy powód pustego dashboardu.

## Uwagi per asystent, które warto znać

### Claude Code

Znaczenie mają cztery wartości, a dwie pierwsze to te, które ludzie pomijają,
robiąc to ręcznie:

- `CLAUDE_CODE_ENABLE_TELEMETRY=1` — przełącznik główny. Bez niego nie jest
  eksportowane nic, niezależnie od reszty ustawień.
- `OTEL_LOGS_EXPORTER=otlp` — to zdarzenia logów niosą sesję. Domyślna
  instalacja emituje metryki i zdarzenia, a nie emituje span-ów, więc ustawienie
  samego eksportera metryk daje sesję niemal pustą.

Dwa opcjonalne dodatki:

```bash
copilotscope connect claude-code --traces    # czas do pierwszego tokenu (beta)
copilotscope connect claude-code --capture   # tekst promptów, odpowiedzi i narzędzi
```

`--traces` włącza betę śledzenia, która jest **jedynym** źródłem czasu do
pierwszego tokenu dla tego asystenta. Schemat span-ów może się jeszcze zmienić;
to właśnie znaczy flaga bety.

Przechwytywanie treści to tutaj trzy osobne zgody, a standardowa zmienna
OpenTelemetry GenAI, która działa dla Copilot CLI, **nie** jest czytana przez
Claude Code.

### Copilot CLI

`COPILOT_OTEL_CAPTURE_CONTENT` nie jest prawdziwą zmienną i po cichu nic nie
robi. CLI stosuje zamiast tego standard OpenTelemetry GenAI, który ustawia
`--capture`.

### Cowork

Konfigurowany we własnym interfejsie aplikacji desktopowej — nie ma pliku do
zapisania. Chce **pełnej ścieżki**, `http://localhost:4318/v1/logs`, a nie
bazowego endpointu, który biorą pozostali. Wymaga planu Team lub Enterprise,
Claude Desktop 1.1.4173 lub nowszego oraz uprawnień administratora organizacji.
Eksportuje wyłącznie zdarzenia logów, więc sesja Cowork nie ma linii kodu ani
czasu do pierwszego tokenu.

## Sprawdź to

```bash
copilotscope doctor
```

Jeśli sesja nadal się nie pojawia, klasyczne przyczyny w kolejności
częstotliwości:

1. VS Code nie został przeładowany.
2. Same podpowiedzi inline — nie było tury czatu ani agenta.
3. Claude Code: brakuje przełącznika głównego albo eksportera logów.
4. Wyeksportowana zmienna `OTEL_EXPORTER_OTLP_ENDPOINT` wygrywa z plikiem
   ustawień dla wszystkiego, co uruchomiono z tej powłoki. `doctor` sprawdza to
   wprost.
5. Korporacyjne ustawienia zarządzane przypinają endpoint do firmowego
   kolektora. Wartości zarządzane zawsze wygrywają.

## Dalej

[Jak czytać sesję](03-reading-a-session.pl.md).
