# 1. Pierwsze uruchomienie

**Sytuacja:** pusta maszyna z Dockerem. **Wynik:** działający stos i oceniona
sesja, którą można przeklikać.

Czas: około pięciu minut, w większości na pobieranie obrazów.

## Instalacja

```bash
curl -fsSL https://raw.githubusercontent.com/konradcinkusz/copilot-scope/master/install.sh | sh
```

Windows, w PowerShellu:

```powershell
irm https://raw.githubusercontent.com/konradcinkusz/copilot-scope/master/install.ps1 | iex
```

Instalator sprawdza Dockera, zapisuje plik compose i skrypt sterujący
`copilotscope` do katalogu `~/.copilotscope`, uruchamia stos, czeka aż kolektor
odpowie i proponuje skonfigurowanie każdego znalezionego asystenta.

**Nie ma czego deklarować.** Żadnego klucza do wygenerowania, żadnej zmiennej
środowiskowej do wyeksportowania, żadnego JSON-a do ręcznej edycji. Każdy port
wiąże się z `127.0.0.1`, a Postgres nie publikuje portu w ogóle, więc na jednej
maszynie poświadczenie niczego by nie chroniło, a kosztowałoby krok
konfiguracyjny w każdym kliencie.

Wolisz sterować Compose samodzielnie? To robi to samo:

```bash
curl -O https://raw.githubusercontent.com/konradcinkusz/copilot-scope/master/docker-compose.ghcr.yml
docker compose -f docker-compose.ghcr.yml up -d
```

## Udowodnij, że potok działa, zanim obwinisz klienta

Zrób to przed konfigurowaniem czegokolwiek. Oszczędza godzinę debugowania
niewłaściwej połowy systemu.

```bash
copilotscope probe
```

Wysyła jedną symulowaną sesję prawdziwym OTLP/HTTP protobuf, a potem sprawdza,
czy kolektor potrafi ją oddać. Jeśli przejdzie, działają przyjmowanie,
dekodowanie, ocena i trwałość — więc wszystko, czego nadal brakuje, jest
konfiguracją klienta, a nie stosu.

## Wyświetl cokolwiek na ekranie

```bash
copilotscope demo     # kilkanaście fabrykowanych sesji
copilotscope open     # http://localhost:5200
```

Sesje zasiane są oznaczone jako `demo`, więc zrzut ekranu z nich nigdy nie
zostanie wzięty za dowód na temat prawdziwego asystenta.
`copilotscope demo demo` ładuje zamiast tego większy, wielodniowy zestaw.

## Używasz już Claude Code? Przeskocz dalej

Claude Code zapisuje każdą sesję na dysk niezależnie od tego, czy telemetria
jest skonfigurowana. Ocena tej historii nie wymaga żadnej konfiguracji klienta:

```bash
copilotscope import --dry-run     # zobacz co znalazł, nie wysyłaj nic
copilotscope import
```

Ponowne uruchomienie jest bezpieczne: sesje zachowują własny identyfikator
Claude Code, więc drugi przebieg zastępuje, a nie duplikuje. Tekst promptów
zostaje poza importem, chyba że podasz `--include-content`.

Sesje zaimportowane są oznaczone jako `imported` i uczciwie niosą niższą
pewność: transkrypt rejestruje tokeny, modele, narzędzia i prawdziwe czasy, ale
nie czas do pierwszego tokenu, decyzje o zmianach ani oceny kciukiem, bo to są
zdarzenia OpenTelemetry, a nie coś zapisywanego do pliku.

## Co masz teraz

| | |
|---|---|
| Dashboard | <http://localhost:5200> |
| Przyjmowanie OTLP | <http://localhost:4318> |
| Zatrzymanie | `copilotscope down` |
| Usunięcie | `copilotscope uninstall` (z `--purge` kasuje też bazę) |

## Dalej

[Podłącz prawdziwego asystenta](02-connect-your-assistant.pl.md), żeby sesje
były Twoje, a nie fabrykowane.
