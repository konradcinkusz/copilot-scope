# Samouczki

Krok po kroku, praktycznie. Każdy zaczyna się od opisanej sytuacji i kończy
czymś, co widać na ekranie.

Uzupełniają, a nie zastępują dwa pozostałe dokumenty:

| Dokument | Do czego służy |
|---|---|
| **Te samouczki** | Zrobienie tego po kolei, za pierwszym razem |
| [Podręcznik](../papers/copilotscope-manual.pl.tex) (PDF, PL/EN) | Zrozumienie całego systemu: architektura, każdy sygnał, ograniczenia |
| [`docs/TUTORIAL.md`](../TUTORIAL.md) | Dokumentacja referencyjna: pełna konfiguracja każdego asystenta plus rozwiązywanie problemów |

Każdy samouczek istnieje po angielsku i po polsku, a kontrola w CI
([`scripts/check-doc-parity.mjs`](../../scripts/check-doc-parity.mjs)) przerywa
build, jeśli jedna połowa zostanie zmieniona bez drugiej — tłumaczenie, które
się rozjeżdża, jest gorsze niż brak tłumaczenia, bo czytelnik mu ufa.

## Po kolei

1. **[Pierwsze uruchomienie](01-first-run.pl.md)** — od pustej maszyny do
   ocenionej sesji. Jedynym wymaganiem jest Docker.
2. **[Podłącz swojego asystenta](02-connect-your-assistant.pl.md)** — prawdziwa
   telemetria z Claude Code, VS Code, Copilot CLI albo Cowork.
3. **[Jak czytać sesję](03-reading-a-session.pl.md)** — co znaczą liczby i na
   której z nich działać.
4. **[Wdrożenie zespołowe](04-team-deployment.pl.md)** — więcej niż jedna
   maszyna, co całkowicie zmienia postawę bezpieczeństwa i prywatności.

## Jeśli coś nie działa

```bash
copilotscope doctor
```

Sprawdza Dockera, kontenery, kolektor, czy wdrożenie jest wystawione bez klucza,
co faktycznie mówi plik ustawień każdego asystenta, czy wyeksportowana zmienna
nie nadpisuje tego pliku i ile transkryptów leży na dysku niezaimportowanych.
