# Dziesięć propozycji prac badawczych — pomiar jakości sesji AI-asystenta

> **Wersja do przedstawienia studentom:** [`research/articles/thesis_topics.tex`](articles/thesis_topics.tex)
> — te same dziesięć tematów sformatowane jako karty prac dyplomowych, **jedna strona A4 na temat**,
> z proponowanym poziomem (inżynierska / magisterska), kryterium akceptacji i punktem wejścia w kodzie.
> PDF (`CopilotScope_Thesis_Topics.pdf`) buduje się razem z artykułem i jest dołączany do każdego
> wydania. Ten plik pozostaje wersją roboczą — dłuższą, bez ograniczenia jednej strony.

> Ten dokument bierze dziesięć algorytmów/koncepcji pomiaru jakości sesji z
> [`docs/ANALYSIS.md`](../docs/ANALYSIS.md) §8 (i tabeli w README) i dla każdego formułuje
> samodzielną propozycję pracy badawczej — coś, co przyszły inżynier lub stażysta może wziąć
> jako temat pracy dyplomowej, wewnętrznego RFC albo tygodniowego spike'a. Sześć z dziesięciu
> ma już działającą implementację w `src/CopilotScope.Collector/Quality/` i notatnik referencyjny
> w tym katalogu (`0X_*.ipynb`) — tam praca badawcza to *rozszerzenie i walidacja*, nie budowa od
> zera. Cztery nie mają implementacji — tam praca badawcza to *projekt i prototyp*.
>
> Numeracja jest zgodna z tabelą "Evaluation algorithms — implementation status" w README, żeby
> odniesienia między dokumentami się nie rozjeżdżały.

---

## 1. LLM-as-a-Judge (G-Eval)

**Status:** ❌ nie zaimplementowany — wymaga agenta-sędziego w chmurze (Azure AI Foundry).

**Problem badawczy.** Model-sędzia ocenia transkrypt sesji względem rubryki (poprawność,
kompletność, styl komunikacji) — najbogatszy sygnał jakościowy dostępny w literaturze, ale i
najdroższy oraz najbardziej podatny na bias. Pytanie nie brzmi "czy G-Eval działa" (działa, to
ugruntowana technika), tylko: da się go wpiąć w CopilotScope bez zerwania zasad projektu
("telemetry-only", "no LLM judge at scoring time" — patrz `research/articles/quality_measurement_framework.tex` §Introduction)?

**Pytania badawcze.**
1. Jak zaprojektować rubrykę G-Eval tak, żeby jej wynik był *porównywalny* z istniejącym
   składanym score 0–100, a nie kolejną, niepowiązaną liczbą?
2. Jaki jest koszt per sesja (tokeny sędziego) w funkcji długości transkryptu, i przy jakim
   progu przestaje się to opłacać względem wartości informacyjnej?
3. Jak mierzyć i raportować bias sędziego (np. długość odpowiedzi jako proxy jakości) zamiast
   go ukrywać za jedną liczbą?

**Proponowana metodologia.** Zbiór ~50–100 sesji z `tools/CopilotScope.Seeder -- demo` (mieszanka
person: clean/error-prone/frustrated) jako zbiór referencyjny. Ręczne etykietowanie (2+ osoby,
miara zgodności międzysędziowskiej, np. Cohen's κ) jako ground truth. G-Eval jako trzeci "sędzia"
porównywany do etykiet ludzkich, nie do istniejącego score'u CopilotScope — inaczej porównujesz
model do samego siebie.

**Kryteria sukcesu.** Korelacja G-Eval ↔ etykiety ludzkie wyższa niż korelacja
istniejącego score'u 0–100 ↔ etykiety ludzkie, przy udokumentowanym koszcie na sesję.

**Ryzyka.** Sędzia oceniający transkrypt inżyniera oceniającego pracę innego inżyniera to
podwójna warstwa nieprzejrzystości — wymaga *twardego* wymogu ujawniania rubryki i przykładów
uzasadnień, nie tylko liczby.

---

## 2. SPUR (Supervised Prompting for User satisfaction Rubrics)

**Status:** ❌ nie zaimplementowany — wymaga agenta-sędziego i oznaczonych sesji treningowych.

**Problem badawczy.** SPUR (arXiv 2403.12388) uczy model interpretowalnych rubryk SAT/DSAT z
etykietowanych rozmów, zamiast oceniać wg statycznej rubryki jak G-Eval. To stan sztuki w
szacowaniu satysfakcji użytkownika (badane m.in. na Bing Copilot), ale wymaga własnego zbioru
etykietowanego — CopilotScope go nie ma.

**Pytania badawcze.**
1. Ile ręcznie etykietowanych sesji (SAT/DSAT) potrzeba, zanim SPUR zacznie generalizować lepiej
   niż statyczna rubryka G-Eval na tym samym zbiorze?
2. Czy sygnał `FrustrationAnalyzer` (algorytm #9, już zaimplementowany lokalnie) może posłużyć
   jako *słaby* wstępny etykietator (weak supervision) zamiast czysto ręcznego etykietowania?
3. Jak rubryki SPUR degradują się przy zmianie asystenta (VS Code → Claude Code → Cursor) — czy
   trzeba osobnego modelu per emiter, czy rubryka transferuje się między dialektami telemetrii?

**Proponowana metodologia.** Pilotaż z małym zbiorem (~200 sesji) etykietowanych przez zespół
używający samego CopilotScope na co dzień (dogfooding — najbardziej wiarygodne etykiety, bo
etykietujący rozumie kontekst pracy). Porównanie SPUR vs. G-Eval (praca #1) na identycznym
zbiorze walidacyjnym.

**Kryteria sukcesu.** SPUR osiąga porównywalną trafność do G-Eval przy niższym koszcie per
inferencję (SPUR nie wymaga długiej rubryki w każdym prompt-cie sędziego).

**Ryzyka.** Rubryka wyuczona na jednym zespole/repo może nie transferować się do innego —
koniecznie testować cross-repo przed uznaniem za gotowe do włączenia do składanego score'u.

---

## 3. Metryki komponentowe RAG (RAGAS)

**Status:** ❌ nie zaimplementowany — dotyczy tylko sesji opartych o retrieval; wymaga treści.

**Problem badawczy.** Faithfulness / answer relevance / context precision mają sens tylko
wtedy, gdy sesja faktycznie robi retrieval (np. asystent przeszukuje dokumentację repo albo
bazę wiedzy przed odpowiedzią) — a obecna telemetria CopilotScope nie odróżnia "sesji z
retrievalem" od "sesji czysto generatywnej". To *praca przygotowawcza przed* jakąkolwiek
implementacją RAGAS.

**Pytania badawcze.**
1. Czy istniejące spany `execute_tool` niosą wystarczający sygnał, żeby wykryć, że dane
   wywołanie narzędzia było retrievalem (np. `search`, `grep`, `read_file` vs. `run_tests`,
   `execute_command`), bez nowej instrumentacji po stronie klienta?
2. Jeśli nie — jaki minimalny atrybut trzeba by dodać do `execute_tool` (np.
   `gen_ai.tool.category=retrieval`), i czy da się to zaproponować jako rozszerzenie konwencji
   OTel GenAI zamiast prywatnego atrybutu CopilotScope?
3. Ile sesji w typowym repo w ogóle kwalifikuje się do oceny RAGAS — czy to niszowy, czy częsty
   przypadek dla docelowych użytkowników?

**Proponowana metodologia.** Audyt istniejących danych z `tools/CopilotScope.Seeder`: policzyć
odsetek sesji z co najmniej jednym wywołaniem narzędzia sklasyfikowanym heurystycznie (po nazwie
narzędzia) jako retrieval. Jeśli odsetek jest niski, to sygnał, że RAGAS zostaje niski priorytet
mimo bogactwa literatury.

**Kryteria sukcesu.** Jasna rekomendacja idź/nie idź poparta danymi z realnych sesji, zanim
ktokolwiek napisze linijkę kodu analizatora.

**Ryzyka.** Najłatwiej ze wszystkich dziesięciu przecenić wartość tej metryki, bo ma najsilniejsze
zaplecze akademickie (RAGAS jest dobrze ugruntowany) — ale dobre zaplecze teoretyczne nie znaczy
dopasowania do tego, co faktycznie robią sesje Copilota.

---

## 4. Edit Survival Analysis

**Status:** ✅ w pełni zaimplementowany — `EditSurvivalAnalyzer`,
[`research/03_edit_survival.ipynb`](03_edit_survival.ipynb).

**Problem badawczy.** Algorytm już działa (four-gram overlap + no-revert split, wagi 0.4/0.6),
ale opiera się na dwóch heurystykach zaimportowanych z telemetrii Copilota bez niezależnej
walidacji na danych CopilotScope. Praca badawcza tutaj to nie budowa, tylko **kalibracja i
kontrola założeń**.

**Pytania badawcze.**
1. Czy podział wag 0.4 (four-gram) / 0.6 (no-revert) jest optymalny, czy arbitralny — jak
   zmienia się trafność predykcji "ten kod faktycznie był użyteczny" przy innych proporcjach,
   testowane na sesjach z ręcznie sprawdzonym outcome (kod wciąż w repo po tygodniu)?
2. Okno "no-revert" (30/300 s) pochodzi z telemetrii GitHuba — czy te same progi mają sens dla
   Claude Code/Cursor, gdzie rytm edycji bywa inny (dłuższe tury agentowe)?
3. Czy da się odróżnić "revert bo kod był zły" od "revert bo user zmienił zdanie co do zadania" —
   obecnie oba wyglądają identycznie w telemetrii.

**Proponowana metodologia.** Wybrać próbkę 20–30 realnych sesji z `research/03_edit_survival.ipynb`
jako punkt startowy, dograć do nich rzeczywisty stan repo tydzień później (czy commit przetrwał w
historii git), porównać z tym, co przewidział survival score.

**Kryteria sukcesu.** Udokumentowana korelacja między survival score a stanem repo po tygodniu,
plus rekomendacja (utrzymać wagi / przestroić / dodać sygnał).

**Ryzyka.** Niskie — to najbardziej dojrzały z dziesięciu algorytmów; ryzyko jest głównie w
nadinterpretacji małej próbki jako dowodu ogólnego.

---

## 5. Acceptance-weighted Throughput

**Status:** ✅ w pełni zaimplementowany — `ThroughputAnalyzer`,
[`research/04_throughput.ipynb`](04_throughput.ipynb).

**Problem badawczy.** Zaakceptowane linie/edycje na interakcję, ważone odrzuceniami — prosty
proxy produktywności, celowo prosty. Praca badawcza: **czy prostota tu szkodzi**? Throughput
nagradza dużo zaakceptowanego kodu, ale nic nie mówi o tym, czy mniej kodu (bo trafniejszego)
nie byłoby lepszym wynikiem.

**Pytania badawcze.**
1. Jak throughput koreluje (dodatnio czy ujemnie) z edit survival (#4) na tym samym zbiorze
   sesji — czy wysoki throughput koniecznie oznacza wysoki survival, czy to dwa niezależne osie?
2. Czy da się zbudować wariant "throughput per złożoność zadania" (np. znormalizowany po liczbie
   zmienionych plików) zamiast czystego LOC — i czy telemetria w ogóle niesie sygnał złożoności?
3. Jaki jest wpływ długości linii/stylu formatowania (np. Prettier vs. ręczne formatowanie) na
   porównywalność LOC między repo?

**Proponowana metodologia.** Scatter plot throughput vs. survival na zbiorze demo z Seedera;
policzyć współczynnik korelacji Pearsona/Spearmana; jeśli korelacja jest słaba lub ujemna dla
podzbioru sesji, to mocny argument za tym, żeby throughput nigdy nie był samodzielną metryką
sukcesu (obecnie i tak wchodzi tylko jako komponent w composite, nie samodzielnie — zweryfikować,
czy waga 0.20 na "acceptance" to dalej ma sens przy takim wyniku).

**Kryteria sukcesu.** Ilościowa odpowiedź na "throughput i survival mierzą to samo czy coś
innego", z rekomendacją dla wag composite engine.

**Ryzyka.** To metryka najbardziej podatna na Goodharta ze wszystkich dziesięciu (README już to
nazywa wprost) — każda praca badawcza nad nią musi kończyć się sekcją "jak to może być
zgamowane", nie tylko "jak poprawić trafność".

---

## 6. Turn-level Friction & Repair Analysis (TFRA)

**Status:** ✅ w pełni zaimplementowany — `SegmentAnalyzer`,
[`research/02_tfra_segment_analyzer.ipynb`](02_tfra_segment_analyzer.ipynb).

**Problem badawczy.** TFRA to jedyny algorytm w tabeli, który dostał w README osobne
uzasadnienie wyboru ("działa wyłącznie na metadanych... latencję ocenia względem mediany
własnej sesji"). To już mocny projekt, ale kary za błędy LLM (−0.35×), błędy narzędzi (−0.15×) i
pętle naprawcze (−0.10×) są dobrane ręcznie, nie wyuczone.

**Pytania badawcze.**
1. Czy te trzy wagi kar odtwarzają intuicję inżynierów, którzy czytają transkrypty ręcznie —
   czy da się to zweryfikować przez porównanie TFRA score z ręczną oceną "ta tura była zła" na
   próbce 50 tur?
2. "Pętla naprawcza" jest zdefiniowana jako seria tool-calli z błędami — jaki próg liczby
   powtórzeń najlepiej odróżnia rzeczywistą pętlę od normalnego cyklu debugowania
   (spróbuj-nie-działa-popraw, który wcale nie jest oznaką złej sesji)?
3. Czy TFRA per-turowy score powinien maleć nieliniowo z liczbą tur w sesji (im dłuższa sesja,
   tym więcej okazji do jednej złej tury, która nie definiuje całości)?

**Proponowana metodologia.** Ręczna adnotacja 50 tur z sesji "curated real conversations"
(Seeder ma gotowe długie, sensowne rozmowy — `Redis rate limiter`, `RDS Postgres upgrade`, itd.)
jako "friction: tak/nie" + porównanie z automatycznym TFRA score.

**Kryteria sukcesu.** Zgodność (accuracy albo κ) automatycznego TFRA z ręczną adnotacją powyżej
ustalonego progu (np. 0.7), z listą przypadków rozbieżnych do analizy jakościowej.

**Ryzyka.** Niskie technicznie — dobrze zaprojektowany algorytm; głównym ryzykiem jest czas
ręcznej adnotacji, nie trudność metodologiczna.

---

## 7. Latency-utility Model

**Status:** ✅ w pełni zaimplementowany — `LatencyUtilityAnalyzer`,
[`research/05_latency_utility.ipynb`](05_latency_utility.ipynb).

**Problem badawczy.** Krzywa log-linear mapująca TTFT na użyteczność opiera się na progach z
badań HCI ogólnych (2 s uwaga, 8 s porzucenie) — nie z badań specyficznych dla programistów
czekających na sugestię kodu, którzy mogą mieć inną tolerancję niż użytkownicy ogólnego czatu.

**Pytania badawcze.**
1. Czy progi 2 s / 8 s trzymają się dla sesji agentowych (Claude Code/Cowork), gdzie
   użytkownik i tak oczekuje wielosekundowego "myślenia" narzędzia, w przeciwieństwie do
   pojedynczej sugestii inline w VS Code?
2. Czy percepcja opóźnienia zależy od tego, czy poprzednia odpowiedź w tej samej sesji była
   szybka czy wolna (efekt kontrastu) — czy krzywa powinna być względna do mediany sesji, tak
   jak już robi to TFRA (#6) dla latencji tury?
3. Jak dużo wariancji w subiektywnej frustracji (feed z algorytmu #9) tłumaczy sama latencja, a
   ile inne czynniki?

**Proponowana metodologia.** Krzyżowa analiza z algorytmem #9 (frustracja): dla sesji z wysoką
latencją p95, sprawdzić czy `FrustrationAnalyzer` wykrywa więcej sygnałów frustracji w
wiadomościach *bezpośrednio po* wolnych turach niż losowo w sesji.

**Kryteria sukcesu.** Ilościowy związek między latency-utility score a niezależnym sygnałem
frustracji, uzasadniający (lub obalający) obecne progi 2 s/8 s dla tej konkretnej domeny.

**Ryzyka.** Progi HCI są dobrze ugruntowane ogólnie — ryzykiem jest przeuczenie się pod bardzo
mały, niereprezentatywny zbiór sesji projektu.

---

## 8. Token & Cache Economics

**Status:** ✅ w pełni zaimplementowany — `TokenEconomicsAnalyzer`,
[`research/06_token_economics.ipynb`](06_token_economics.ipynb).

**Problem badawczy.** Koszt per model liczony jest z konfigurowalnego cennika
(`CopilotScope:Pricing`) — poprawny matematycznie, ale wrażliwy na to, że dostawcy zmieniają
ceny częściej niż ktokolwiek aktualizuje plik konfiguracyjny. To praca bardziej operacyjna niż
algorytmiczna.

**Pytania badawcze.**
1. Jak duży błąd we wskazywanych oszczędnościach cache generuje nieaktualny cennik po np. 3
   miesiącach — czy warto zbudować alert "cennik nie był aktualizowany od X dni"?
2. Czy oszczędności z prompt-cache raportowane przez różnych emiterów (VS Code vs. Claude Code)
   są w ogóle porównywalne, czy różne strategie cache'owania po stronie dostawców czynią
   porównanie międzyemiterowe mylące?
3. Czy "koszt per zaakceptowaną edycję" powinien uwzględniać koszt *odrzuconych* prób w tej
   samej turze, czy tylko koszt tury, która się powiodła?

**Proponowana metodologia.** Przegląd historyczny cenników 2-3 dostawców z ostatnich 6 miesięcy;
symulacja wpływu nieaktualnego cennika na raportowany koszt sesji z Seedera.

**Kryteria sukcesu.** Konkretna rekomendacja procesu (np. kwartalny przegląd cennika,
automatyczny warning) plus test wrażliwości pokazujący, jak bardzo błędny cennik zniekształca
wnioski.

**Ryzyka.** Niskie merytorycznie, wysokie operacyjnie — to praca, którą łatwo odłożyć w
nieskończoność, a jej brak cicho psuje jedną z sześciu w pełni działających metryk.

---

## 9. Klasyfikacja frustracji użytkownika

**Status:** ✅ uproszczony lokalnie (`FrustrationAnalyzer`,
[`research/07_frustration_analyzer.ipynb`](07_frustration_analyzer.ipynb)) · 🔜 głęboki wariant
w chmurze zaplanowany.

**Problem badawczy.** Leksykon EN/PL + Jaccard przeformułowań + sygnały typograficzne to
świadomie "zaszumiona" heurystyka (README to mówi wprost), dlatego **report-only** i poza
składanym score'em. Ścieżka awansu do score'u wymaga walidacji, nie tylko więcej reguł.

**Pytania badawcze.**
1. Jaki jest rzeczywisty odsetek fałszywych trafień ("no worries", "nice, that works!" z
   negacją-złapaną-źle) na realnym zbiorze rozmów, w PL i EN osobno?
2. Czy klasyfikator jest ślepy na sarkazm w sposób systematyczny (np. zawsze przy krótkich,
   entuzjastycznie brzmiących zdaniach) — da się to skatalogować jako znane ograniczenie z
   przykładami?
3. Ile etykietowanych przykładów potrzeba, żeby SPUR (#2) lub lekki klasyfikator wyuczony
   przebił heurystykę leksykalną na tym samym zbiorze walidacyjnym?

**Proponowana metodologia.** Zbiór 100+ wiadomości z sesji "frustrated persona" w Seederze +
100 z "clean persona" jako kontrola negatywna; ręczne etykietowanie; macierz pomyłek dla
obecnego heurystycznego klasyfikatora osobno dla PL i EN.

**Kryteria sukcesu.** Udokumentowany precision/recall heurystyki jako baseline, z jasnym progiem
("promocja do składanego score'u wymaga precision ≥ X"), zamiast subiektywnej oceny "wygląda
nieźle".

**Ryzyka.** Frustracja to jedyny z dziesięciu algorytmów, który dotyka emocji człowieka wprost —
każda praca nad nim musi utrzymać zasadę report-only, dopóki walidacja nie jest jednoznaczna;
pokusa "wygląda wystarczająco dobrze, wrzućmy do score'u" jest dokładnie tym, przed czym README
ostrzega.

---

## 10. Task-completion Detection

**Status:** ❌ nie zaimplementowany · ⚠️ częściowe hooki lokalnie (sygnały build/testy przez API
ingest) · ✅ pełny wariant w chmurze zaplanowany.

**Problem badawczy.** Najbliższe pytaniu "czy praca została faktycznie wykonana", i najtrudniejsze
do zautomatyzowania z całej dziesiątki — telemetria Copilota sama z siebie nie mówi, czy build
przeszedł ani czy testy są zielone.

**Pytania badawcze.**
1. Jaki minimalny zestaw zewnętrznych sygnałów (exit code builda, wynik testów, `git status`
   czysty po sesji) wystarcza, żeby odróżnić "zadanie domknięte" od "sesja się urwała"?
2. Czy da się to zbudować jako hook CI (np. krok w GitHub Actions, który POST-uje wynik do
   istniejącego API ingest) zamiast wymagać nowej integracji po stronie edytora?
3. Ile sesji kończy się bez żadnego zewnętrznego sygnału zamknięcia (np. deweloper po prostu
   przestał pisać) — i czy to w ogóle da się odróżnić od "zadanie skończone, ale nikt tego nie
   raportuje" bez sygnału-judge'a w chmurze?

**Proponowana metodologia.** Prototyp: skrypt CI, który po `dotnet test` POST-uje
`{"sessionId": ..., "buildPassed": bool, "testsGreen": bool}` na istniejący endpoint ingest;
test na 5–10 realnych sesji programistycznych w tym repo (dogfooding na CopilotScope samym
sobą — sesje pracy nad tym repo).

**Kryteria sukcesu.** Działający end-to-end przykład: sesja → PR → CI → sygnał domknięcia
widoczny w dashboardzie, zanim ktokolwiek projektuje wariant chmurowy z agentem-sędzią.

**Ryzyka.** Największe ryzyko projektowe z dziesięciu: łatwo zbudować coś, co wymaga integracji
per-CI-system (GitHub Actions dziś, co jutro?) i staje się kolejnym niedokończonym adapterem.
Warto zacząć od jednego systemu CI (ten, którego repo już używa) zamiast projektować uniwersalne
API na wyrost.

---

## Jak korzystać z tego dokumentu

Każda z dziesięciu propozycji jest zaprojektowana jako **samodzielny temat** — nie trzeba robić
wszystkich dziesięciu po kolei, i nie ma między nimi twardych zależności poza tym, że #1/#2
(G-Eval/SPUR) dzielą tę samą potrzebę zbioru etykietowanego, więc warto je łączyć w jeden pilotaż
etykietowania zamiast dwóch osobnych. Punktem wejścia dla implementacji jest zawsze
`Quality/Insights.cs` (interfejs `IInsightAnalyzer`) — jedna klasa + jedna rejestracja DI, zero
pracy w UI, zgodnie z zasadą opisaną w README.
