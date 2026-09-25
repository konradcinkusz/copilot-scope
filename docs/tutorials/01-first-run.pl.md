# 1. Pierwsze uruchomienie

**Sytuacja:** pusta maszyna. **Wynik:** działający CopilotScope i Twoje własne sesje ocenione
na ekranie.

Czas: około dwóch minut. Niczego nie trzeba instalować wcześniej: ani Dockera, ani .NET, ani
żadnego środowiska uruchomieniowego.

## Instalacja

```bash
curl -fsSL https://raw.githubusercontent.com/konradcinkusz/copilot-scope/master/install.sh | sh
```

Windows, w PowerShellu:

```powershell
irm https://raw.githubusercontent.com/konradcinkusz/copilot-scope/master/install.ps1 | iex
```

Instalator robi cztery rzeczy:
- pobiera jeden samodzielny program, `copilotscope`, dla Twojego systemu;
- sprawdza go z plikiem `SHA256SUMS` wydania i odrzuca pobranie, które się nie zgadza;
- instaluje go w `~/.copilotscope/app` i dodaje do ścieżki PATH;
- proponuje skierowanie do niego znalezionych asystentów, pokazując każdą zmianę, zanim ją
  wprowadzi.

Możesz odmówić wszystkim: samouczek 2 robi to po jednym.

**Nie ma czego deklarować.** Żadnego klucza do wygenerowania, żadnej zmiennej środowiskowej do
wyeksportowania, żadnego JSON-a do ręcznej edycji. Wszystko wiąże się z `127.0.0.1`, więc na
jednej maszynie poświadczenie niczego by nie chroniło.

## Uruchomienie

```bash
copilotscope
```

```text
CopilotScope 1.1.0 is running.
  Dashboard   http://localhost:5200
  Telemetry   http://localhost:4318   (point your assistant's OTLP/HTTP exporter here)
  Sessions    /home/you/.copilotscope/data
  History     Claude Code, read from /home/you/.claude/projects (never changed)
  Assistants  Claude Code sends telemetry here; VS Code could too: `copilotscope setup` (it asks first)
```

Dashboard otwiera się w przeglądarce. Ctrl+C zatrzymuje wszystko; sesje zostają w
`~/.copilotscope/data` na następne uruchomienie.

## Używasz już Claude Code? To już tam jest

Claude Code zapisuje każdą sesję na dysk niezależnie od tego, czy telemetria jest
skonfigurowana. CopilotScope czyta tę historię przy starcie, bez kroku importu, i po kilku
sekundach dashboard pokazuje Twoje wcześniejsze sesje, ocenione.

Sesja, która wciąż jest zapisywana, czeka, aż będzie cicha przez dziesięć minut. Dzięki temu
telemetria na żywo — jeśli asystent ją wysyła — nie zostanie policzona dwa razy.

```bash
copilotscope scan       # przeczytaj teraz i powiedz, co znaleziono
```

Sesje zaimportowane są oznaczone jako `imported` i uczciwie niosą niższą pewność. Transkrypt
rejestruje tokeny, modele, narzędzia i prawdziwe czasy, ale nie czas do pierwszego tokenu,
decyzje o zmianach ani oceny kciukiem: to są zdarzenia OpenTelemetry, a nie coś zapisywanego do
pliku. Tekst promptów nigdy nie jest importowany.

Tak wygląda strona, kiedy ma już co pokazać — lista sesji po lewej, a po prawej ocena tej, którą
wybrano:

![Strona Sessions](../img/dashboard-sessions.png)

Wynik jest nagłówkiem; widok `View: Basic` ogranicza stronę do niego. Samouczek 3 rozkłada tę
samą stronę na panele.

## Sprawdź całą ścieżkę

```bash
copilotscope doctor
```

Sprawdza:
- czy CopilotScope działa i czy pliki dashboardu są na miejscu;
- co naprawdę mówią ustawienia każdego asystenta i dokąd wskazują;
- czy zmienna wyeksportowana w Twojej powłoce ich nie nadpisuje;
- ile historii leży na dysku.

## Co masz teraz

| | |
|---|---|
| Dashboard | <http://localhost:5200> |
| Przyjmowanie OTLP | <http://localhost:4318> |
| Sesje | `~/.copilotscope/data` — usuń, żeby zacząć od nowa |
| Zatrzymanie | Ctrl+C albo `copilotscope stop` z innego terminala |
| Usunięcie | `copilotscope disconnect`, potem usuń `~/.copilotscope` i zdejmij `copilotscope` ze ścieżki PATH |

Dla zespołu, współdzielonego serwera albo z Grafaną obok nadal jest stos Docker Compose:
zobacz [samouczek 4](04-team-deployment.pl.md).

## Dalej

[Podłącz prawdziwego asystenta](02-connect-your-assistant.pl.md), żeby nowe sesje przychodziły
w trakcie pracy, z opóźnieniami i decyzjami o zmianach, których transkrypt nie zapisuje.
