# 3. Jak czytać sesję

**Sytuacja:** przychodzą prawdziwe sesje. **Wynik:** potrafisz odróżnić sesję
złą od źle zmierzonej i wiesz, na której liczbie działać.

## Czytaj w tej kolejności

### 1. Ocena i pewność, razem

Nigdy sama ocena. Pewność to pokrycie danymi pomnożone przez rampę liczby
próbek i jest eksportowana obok każdej oceny nie bez powodu: **90 zbudowane na
czterech próbkach znaczy mniej niż 70 zbudowane na czterdziestu.**

| Wskaźnik | Ocena |
|---|---|
| 85+ | doskonale |
| 70+ | dobrze |
| 55+ | przeciętnie |
| 40+ | słabo |
| poniżej | krytycznie |

### 2. Najgorsza tura wraz z powodami

To część, na której można działać, i to jest rzecz, której dashboard zużycia
strukturalnie nie potrafi dać. Każdy trace `invoke_agent` to jedna tura,
oceniana pod kątem:

- błędów LLM i narzędzi;
- opóźnienia względem mediany czasu do pierwszego tokenu **tej właśnie sesji**;
- pętli naprawczych — serii wywołań narzędzi zawierających niepowodzenia.

Mediana liczona per sesja jest tu istotą. Globalny próg opóźnienia nie odróżni
wolnej tury od wolnego modelu; sesja porównana sama ze sobą — owszem.

### 3. Składowe

Wskaźnik 62 napędzany opóźnieniem to inny problem niż 62 napędzane
niezawodnością, i mają różne naprawy.

| Składowa | Waga | Jak ją czytać |
|---|---|---|
| Niezawodność | 0,25 | Kwadrat odsetka wywołań bez błędu. Kilka błędów kosztuje więcej niż liniowo |
| Akceptacja | 0,20 | Zawsze czytaj razem z przeżywalnością zmian, nigdy osobno |
| Tarcie | 0,20 | Średnia ocena tury z punktu wyżej |
| Opóźnienie | 0,15 | Ile leżało powyżej progów 2 s uwagi i 8 s porzucenia |
| Oceny | 0,10 | Kciuki, tam gdzie asystent je raportuje |
| Efektywność | 0,10 | Koszt na turę i na przyjętą zmianę |

**Do wskaźnika wchodzą tylko składowe z danymi**, a wagi są po nich
renormalizowane. Sesja bez telemetrii zmian i ocen jest oceniana po tym, co
faktycznie wyprodukowała, a nie przypinana do neutralnego prioru.

### 4. Przeżywalność zmian

Akceptacja bez przeżywalności to sygnatura kodu przyjętego i zaraz cofniętego.
Dlatego akceptacja to tylko 0,20 i jest sparowana z kontrmiarą: naciskaj na samą
akceptację, a nagrodzisz przyjmowanie złych podpowiedzi.

### 5. Ekonomia tokenów

Koszt na przyjętą zmianę to liczba, która zamienia rozmowę o jakości w rozmowę o
budżecie.

## Zła sesja czy źle zmierzona?

To rozróżnienie jest najczęstszym błędem odczytu, a dashboard daje wszystko, co
potrzebne, żeby je zrobić.

| Objaw | Prawdopodobny odczyt |
|---|---|
| Niska ocena, wysoka pewność, jasna najgorsza tura | Sesja naprawdę zła. Działaj na turze |
| Niska ocena, niska pewność | Za mało sygnału. Sprawdź, co asystent faktycznie emituje |
| Ocena spadła *i* pewność spadła | Zmiana podstawy pomiaru, a nie regresja — kohorta przestała raportować sygnał |
| Zaimportowana sesja z dziwnym wynikiem | Spodziewane. Nie ma w ogóle sygnału opóźnienia, decyzji o zmianach ani ocen |

Trzeci wiersz to także sposób działania alertów: spadek, któremu towarzyszył
spadek pewności, jest raportowany jako zmiana podstawy, a nie regresja, bo
wysyłanie zespołu w pogoń za zmianą, która nigdy nie zaszła, to sposób, w jaki
wycisza się kanał alertów.

## Porównywanie między asystentami

Ostrożnie albo wcale. Oceny są **porównywalne wewnątrz asystenta i kierunkowe
pomiędzy nimi**. Sesja Claude Code nie ma kciuków, a bez bety śledzenia nie ma
czasu do pierwszego tokenu, więc jej 80 opiera się na mniejszym materiale niż 80
sesji VS Code.

Dla uczciwego porównania utrzymaj stały zestaw sygnałów, a nie tylko wskaźnik.
Pełna macierz jest w [`docs/SIGNAL_COVERAGE.md`](../SIGNAL_COVERAGE.md).

## Dwie rzeczy, którymi ocena nie jest

- **Nie jest werdyktem.** Czytaj składowe, a nie nagłówek.
- **Nie dotyczy osoby.** Ocena dotyczy sesji. W żadnym widoku ani eksporcie nie
  ma wymiaru per programista, a testy tego pilnują.

## Gdzie spisano uczciwe ograniczenia

Wskaźnik złożony **nie** został skalibrowany względem ocen ludzkich. Jest
przemyślanym osądem, a nie dopasowanym modelem, a każda ocena sędziego jest
kierunkowa i niczego nie blokuje. [`docs/CALIBRATION.md`](../CALIBRATION.md)
opisuje metodę, a panel etykietowania, który by to naprawił, jest oddalony o
jedną flagę konfiguracyjną.

## Dalej

[Wdrożenie zespołowe](04-team-deployment.pl.md) — które całkowicie zmienia
postawę bezpieczeństwa i prywatności.
