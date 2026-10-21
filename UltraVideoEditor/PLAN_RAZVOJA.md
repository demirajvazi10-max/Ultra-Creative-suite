# Ultra Video Editor: plan razvoja do verzije 2.0

Napravljeno 12. oktobra 2026, posle drugog prolaska kroz ceo kod (112 fajlova, oko 39.600 linija C#).

Kako se čita: plan ima pet faza, od najhitnijeg ka najambicioznijem. Svaka stavka ima oznaku (na primer 1.3), kratko objašnjenje šta je problem i šta treba uraditi, i veličinu posla: mali (do sat vremena), srednji (pola sesije), veliki (cela sesija ili više). Pod "sesijom" se misli na jedan razgovor kao današnji, posle koga ti uradiš build i testiraš.

Naslovi su na nivou 2 i 3, pa se kroz plan može ići tasterom H u JAWS-u.

## Redosled rada od 13. oktobra 2026: montaža bez vida, kao u Premiere-u

Cilj, tvojim rečima: slep čovek u Ultra editoru radi komforno, kao videći u Premiere-u. Seče, spaja, pravi i menja šta god hoće i kako god hoće, bez ijednog problema. Zato je ovo sada glavni redosled, a ostatak plana ispod služi kao spisak stavki.

### R1 Završiti AI kreator
**Status: urađeno 16. oktobra 2026** (peti test: bez primedbi, render bez greške; ostaje provera sa videćom osobom). Za kasnije: pravljenje traje oko dva sata (najviše vremena odnosi Qwen, oko 30 s po klipu), a stih „Mogu poći baka i deka“ je u poslednjem testu ostao bez sopstvenog klipa (prethodni kadar traje duže). Ranije: Testovi „Pesme o šetnji“: Qwen sada stvarno gleda svaki klip, ambijent se meri i čuje, godišnja doba se poklapaju sa stihom, a neprikladni kadrovi se odbacuju. Još jedan do dva kruga testa sa Geminijevim opisom. Freesound (ambijentalni zvukovi) i Coverr (video) čekaju dok se ne vidi da li fali materijala.

### R2 Pregled glavnog prozora iz ugla slepog montažera
Prolazak kroz svaku komandu, prečicu i dijalog glavnog prozora. Rezultat je tačan spisak u ovom planu:
- šta radi kako treba;
- šta radi, ali nezgodno;
- šta fali u odnosu na Premiere.

Usput se ispravljaju sitnice koje se nađu. Jedna sesija.

**Status: prvi deo urađen 13. oktobra 2026.** Nađeno i ispravljeno:
- tvoji snimci su u renderu dobijali boje, šum-filter i Ken Burns pomeranje kao AI klipovi; sada ostaju kakvi jesu;
- svi video klipovi su renderovani bez svog zvuka; sada se zvuk tvojih snimaka čuje na svom mestu;
- efekti (svetlina, kontrast, zamućenje) i brzina klipa nisu stizali do rendera; sada stižu, kao i jačina muzike;
- Ctrl+Z, Ctrl+Y, Ctrl+A, Ctrl+I, Ctrl+Shift+I, Ctrl+Shift+E i F1 nisu radili (stajali su samo kao natpis u meniju); sada rade;
- pomeranje kroz klip tastaturom: Alt+strelice (5 s) i Alt+Shift+strelice (desetinka), uz najavu pozicije;
- pogrešna prečica za prethodni marker u meniju i pomoći;
- „Opiši sve klipove“ sada koristi Qwen.

Rešeno kasnije u R5: brzina klipa ima polje (Ctrl+Alt+S). Provereno: tvoje slike NE dobijaju AI izgled (samo se uklope u kadar); sada dobijaju efekte iz kartice Efekti i pravilno se okreću (slike sa telefona su ranije mogle da izađu postrance).
Ostaje za R5: zvučni efekti (bas, visoki tonovi, eho) nemaju polje. Ctrl+X ostaje „iseci na poziciji“ (odlučeno).

### R3 Slušanje cele montaže bez rendera — URAĐENO (čeka test)
Isto što i 3.1: pesma, ambijent, efekti i govor se čuju zajedno, sa jačinama i pozicijama sa tajmlajna, od mesta na kome stojiš. Najvažnija funkcija koja je falila.

Urađeno:
- Ctrl+Shift+Razmak (i meni Vremenska linija, i dugme 🎧 pored Pusti) za par sekundi miksuje sav zvuk montaže po istim pravilima kao render i pušta ga od mesta gde stojiš ili od izabranog klipa;
- isti taster pauzira i nastavlja, Alt+strelice premotavaju kroz celu montažu;
- dok svira, najavljuje se ime kadra koji je u tom trenutku na slici (i tekst na njemu); može da se isključi u meniju;
- miks se pamti dok se tajmlajn ne promeni, pa drugo puštanje kreće odmah.

Ostalo za kasnije:
- slika se ne prikazuje uz miks (samo zvuk); za videće to bi bio brz pregled niske rezolucije;
- klipovi sa promenjenom brzinom se ne čuju (isto kao u renderu).

### R4 Opis kadra na kome stojiš, i za tvoje snimke — URAĐENO (čeka test)
Isto što i 4.1, ali za SVE klipove, ne samo za one koje AI kreator skine.

Urađeno:
- Ctrl+Shift+F: Qwen opiše tačno kadar na kome stojiš (ko je, šta radi, gde, krupni ili široki plan, tekst ako ga ima), na jeziku programa; radi i za slike i dok svira cela montaža;
- Ctrl+Alt+D: izabrani klip se podeli na delove po promenama kadra (2–8 sekundi) i svaki deo se opiše u pozadini, dok ti radiš dalje; meni AI ima i „Opiši sve moje klipove po delovima“ i „Zaustavi opisivanje“;
- opisi se čuvaju u projektu; dok puštaš, premotavaš, ideš kadar po kadar ili J/L, čuješ opis dela u koji ulaziš; u miksu cele montaže uz ime kadra čuješ i njegov opis; može da se isključi.

Drugačije od plana:
- klipovi se ne opisuju sami čim ih ubaciš, nego na zahtev (Ctrl+Alt+D ili „Opiši sve moje klipove“), da Qwen ne bi radio u pozadini bez tvog znanja dok radi AI kreator. Ako želiš automatski, lako se doda.

### R5 Precizna montaža tastaturom — PRVI DEO URAĐEN (čeka test)
Ono na šta su montažeri navikli, sa najavama za čitač ekrana.

Urađeno:
- J, K, L: unazad, stani (sa tačnom pozicijom), napred; ponovni pritisak ubrzava;
- zarez i tačka: kadar unazad i unapred, uz najavu vremena na stotinku;
- Q i W: skraćivanje početka i kraja klipa do mesta na kome stojiš, bez rupe (ripple), uz najavu nove dužine, pa klip odmah zasvira da se čuje rez;
- Ctrl+Alt+S: brzina klipa (0,25× do 4×) iz pristupačne liste; i pregled u editoru sada svira tom brzinom;
- Delete je već bio ripple (klipovi posle se pomeraju unapred);
- premotavanje više ne izlazi van klipa;
- Ctrl+Razmak, Ctrl+Shift+Razmak i Alt+strelice rade i kad je fokus u listi klipova.

Drugi deo, urađeno (čeka test):
- jačina kroz vreme: Ctrl+Shift+V postavlja tačku jačine gde stojiš, jačina ide glatko od tačke do tačke; u meniju čitanje i brisanje tačaka;
- „Stišaj muziku ispod govora“: muzika pada na 30 % gde god se čuje govor ili zvuk tvog snimka, sa blagim prelazom; upisuje obične tačke, pa se može popraviti i vratiti sa Ctrl+Z;
- zvučni efekti (bas, visoki tonovi, eho, kompresor) iz kartice Efekti sada se čuvaju na klipu sa Ctrl+Shift+E i čuju se u renderu i u miksu montaže;
- render, miks montaže i pregled klipa slušaju iste tačke jačine;
- odlučeno: „iseci na poziciji“ ostaje Ctrl+X.

Time je R5 završen, osim onoga što test pokaže.

Veliki posao, dve do tri sesije.

### R6 Provera sa NVDA i Narratorom — PRIPREMA URAĐENA, čeka test sa NVDA
Isto što i 2.8. Program je proveren samo sa JAWS-om; treba da radi jednako i sa besplatnim čitačima.

Urađeno:
- program prepoznaje koji čitač radi; sa NVDA ili Narratorom (bez JAWS-a) najave idu kao UI Automation obaveštenje, koje oba čitaju pouzdano, umesto „live region“ trika napravljenog za JAWS (koji NVDA ume da pročita dvaput ili nijednom);
- sa JAWS-om je sve isto kao do sada.

Ostaje: proći program sa NVDA (lista klipova, meniji, dijalozi, najave pozicije, miks montaže, opisi) i zapisati šta ne čita.

### R7 Grafički tajmlajn za videće — PRVA VERZIJA URAĐENA (čeka test sa videćom osobom)
Isto što i 3.3: trake, prevlačenje mišem, talasni oblik zvuka, sličice kadrova, kao drugi prikaz iste montaže. Lista ostaje za tastaturu, grafika za miš, a izmena u jednom se odmah vidi u drugom.

Urađeno:
- ispod liste klipova: četiri trake, blokovi klipova sa sličicom ili talasnim oblikom, lenjir sa vremenom, markeri i glava za puštanje;
- klik bira klip i stavlja glavu tu, dupli klik pušta, klik na lenjir pomera glavu;
- prevlačenje pomera klip (i na drugu video ili audio traku), prevlačenje ivice skraćuje; lepljenje za ivice klipova, markere i glavu, Shift isključuje;
- Ctrl+točkić zumira; prikaz prati glavu dok svira; može da se sakrije u meniju Prikaz;
- svaka izmena ide u Ctrl+Z, lista se odmah osveži i izmena se najavi;
- ispravljeno: Ctrl+Z je brisao tačke jačine, zvučne efekte i opise klipa.

Ostalo za kasnije (posle testa sa videćom osobom):
- boje prate samo tamnu temu;
- izbor više klipova mišem, desni klik meni na bloku, prikaz tačaka jačine kao linije.

### Posle toga
Faza 2 (čarobnjak, instaler, uputstvo, licence) pre nego što program ponudiš drugima, pa ostatak faze 3 i 4.

**Procena za R1 do R6: oko 10 do 15 sesija. R7 je posao za sebe, još 4 do 6 sesija.**

## Faza 0: brze ispravke

Sitne stvari koje sam našao u ovom prolazu. Sve zajedno staju u jednu sesiju.

### 0.1 Broj verzije
**Status: urađeno 12. oktobra 2026.**
U UltraVideoEditor.csproj je i dalje pisalo 1.0.3, iako je na GitHub-u već verzija 1.3.0. Ovo izdanje je 1.4.0. Bez toga provera ažuriranja i prozor "O programu" prijavljuju staru verziju. Mali posao.

### 0.2 Stavke lista koje čitač ekrana može da pročita kao naziv klase
**Status: urađeno 12. oktobra 2026.**
U prozoru sa logom, u grupnom izvozu, u listi animacionih scena, u listi kadrova Video Engine-a i u nalazima pregleda videa, stavke su objekti bez teksta za čitač ekrana. WPF u tom slučaju za ime stavke često uzima naziv klase, pa NVDA (a ponekad i JAWS) čita na primer "UltraVideoEditor.LogEntry" umesto poruke. Rešenje je da svaka takva klasa vrati čitljiv tekst, ili da stavka dobije ime iz povezanog podatka. Mali posao, a veliki dobitak za čitače ekrana.

### 0.3 Timeline AI asistent briše i muziku
**Status: urađeno 12. oktobra 2026.**
Komande kao "zadrži samo klipove sa licima" ili "zadrži prvih 10" primenjuju se na sve stavke, uključujući pesmu, ambijentalne zvuke i titlove. Pesma nema "lice" u opisu, pa može da nestane sa tajmlajna. Komande za sortiranje uz to menjaju fiksne pozicije klipova još dok je prozor otvoren, pa Cancel ne vraća stanje. Asistent treba da radi samo nad slikama i video klipovima, a pri odustajanju da vrati snimak stanja. Mali do srednji posao.

### 0.4 Faza 3 (Highlight izvoz) izvozi ceo izvorni video
**Status: urađeno 12. oktobra 2026.**
Izvoz u YouTube, Reels i MP3 formate uzima izvorni video, a ne isečeni highlight. Prelazi koje Faza 3 izračuna nigde se ne koriste. Pošto je miks zvuka dužine highlight-a, a slika dužine celog izvornog videa, rezultat je pogrešan. Treba prvo renderovati highlight (HighlightRenderer to već ume), pa tek njega izvoziti u formate. Srednji posao.

### 0.5 Pitanje "Sačuvati izmene?" pri zatvaranju
**Status: urađeno 12. oktobra 2026.**
Program se zatvara bez pitanja, čak i kad projekat nije sačuvan. Treba pratiti da li ima nesačuvanih izmena i pitati pri zatvaranju, otvaranju drugog projekta i pravljenju novog. Mali posao.

### 0.6 Bezbedno snimanje projekta
**Status: urađeno 12. oktobra 2026.**
Projekat se upisuje direktno preko starog fajla. Ako struja nestane ili se program sruši tokom snimanja, projekat ostaje oštećen. Treba pisati u privremeni fajl, pa ga zameniti (File.Replace), i čuvati jednu rezervnu kopiju (.bak). Mali posao.

### 0.7 Klipovi AI kreatora žive u Temp folderu
**Status: urađeno 12. oktobra 2026.**
AI kreator skida klipove u %TEMP%\UVE_Story_.... Windows (Storage Sense, čišćenje diska) taj folder vremenom briše, pa projekat otvoren posle nekoliko nedelja nema klipove. Uz to se ti folderi nikad ne brišu sami i zauzimaju disk. Klipove treba premestiti u folder projekta ("Media" pored .iskra fajla) kad se projekat sačuva. Srednji posao.

### 0.8 Čišćenje
**Status: delimično urađeno.** PixabaySharp, ApplyFixedPositions i prazna petlja su uklonjeni. Za FoundryIQClient i RealtimeEffects čeka se tvoja odluka. Stare kopije u folderu .github treba obrisati ručno; ne smetaju build-u, jer .NET preskače foldere čije ime počinje tačkom.
- Paket PixabaySharp je u projektu, a nigde se ne koristi.
- FoundryIQClient i RealtimeEffects se nigde ne pozivaju. FoundryIQ verovatno ostaje iz nekog izazova; odluči da li ga čuvamo.
- ApplyFixedPositions u MainWindow je mrtav kod, isto kao i petlja u HighlightCreatorDialog koja nikad ništa ne doda.
- U folderu .github su stare kopije MainWindow.xaml.cs i Models.cs iz aprila. Mogu da zbune i tebe i nekog ko gleda repozitorijum.

Mali posao.

### 0.9 Sitnice u dijalozima
**Status: delimično urađeno.** Pixabay sada najavljuje broj rezultata, Faza 3 ima pravi izbor foldera, poruka o grešci FFmpeg-a više nije prazna i log je ograničen na 5.000 poruka. Zaključano polje za Pixabay ključ rešava se zajedno sa stavkom 1.4.
- Pixabay pretraga ne najavljuje broj rezultata. Tekst se menja, ali nije označen kao živa oblast, pa ga čitač ekrana ne izgovori.
- U Pixabay pretrazi sačuvan ključ ne može da se promeni iz tog prozora (polje je zaključano).
- Faza 3 bira izlazni folder preko trika sa dijalogom za snimanje fajla. .NET 8 ima pravi dijalog za izbor foldera (OpenFolderDialog).
- Kad FFmpeg u Fazi 3 padne, poruka o grešci je obično prazna, jer se uzima poslednji red izlaza, a on je prazan.
- Prozor sa logom raste bez ograničenja tokom dugog rada.

Mali posao.

**Ukupno za fazu 0: jedna sesija. Urađena 12. oktobra 2026, čeka build i test.**

## Faza 1: stabilnost

Cilj: program na koji se možeš osloniti kad praviš spot za pesmu, bez iznenađenja.

### 1.1 Provera svega iz verzije 1.4 u radu
**Status: urađeno 16. oktobra 2026.** Pet krugova testa „Pesme o šetnji“ sa Geminijevim opisom; peta verzija prošla bez primedbi i bez grešaka u logu. Usput je nađena i ispravljena greška u transkripciji: program je Whisperu slao opciju `--vad_parameters`, koju faster-whisper-xxl nema, pa se Whisper odmah gasio.
Sve iz prethodna dva kruga je provereno samo prevođenjem koda, ništa nije pokrenuto. Spisak za testiranje:
- sečenje i markeri;
- render sa prelazima;
- AI kreator od početka do kraja;
- izvoz u MP3, WebM i AVI;
- otvaranje starog projekta;
- Video Engine sa ambijentom;
- pregled videa.

Ovo radiš ti, ja popravljam ono što prijaviš. Srednji posao, pola dana tvog vremena.

### 1.2 Automatski testovi
**Status: urađeno 12. oktobra 2026** (prvi set). Folder Tests: raspored tajmlajna, normalizacija srpskog, poravnanje teksta, prelazi, prepoznavanje tipa fajla. Testovi za FFmpeg komande rendera dolaze kad se RenderEngine razbije na manje delove (3.4), jer su ti delovi sada skriveni unutar klase.
Uz glavni projekat treba dodati poseban testni projekat (xUnit). Prvo bi se testirali delovi bez prozora:
- raspored tajmlajna (TimelineLayout);
- sastavljanje FFmpeg komandi u RenderEngine-u, sa proverom da su brojevi sa tačkom;
- poravnanje teksta (LyricAligner);
- normalizacija srpskog (SerbianText);
- pravila za ambijentalne zvuke.

Ja ne mogu da pokrećem .NET ovde, pa testove pišem ja, a ti ih pokrećeš iz Visual Studija (Test Explorer, jedan taster). Posle toga svaka moja izmena može da se proveri za par sekundi. Veliki posao, ali se najbrže isplati.

### 1.3 Greške koje se tiho gutaju
**Status: urađeno 12. oktobra 2026.** 22 mesta gde se nešto stvarno pokvari sada se beleže u dnevni log fajl i u prozor sa logom. Ostalih stotinak namerno ostaju tiha: to su brisanje privremenih fajlova i gašenje procesa koji su se već ugasili. Neočekivane greške takođe idu u log, a poruka kaže gde je fajl.
U kodu ima oko 120 praznih catch blokova: greška se desi, program je ignoriše i korisnik ne sazna ništa. Najviše ih je u:
- AIVideoCreator (29);
- MainWindow (14);
- VideoReviewDialog (13);
- YouTube dijalogu (10).

Treba proći svaki i odlučiti: zabeležiti u log, najaviti korisniku ili ostaviti (samo kod brisanja privremenih fajlova). Za slepog korisnika tišina je najgori ishod. Srednji do veliki posao.

### 1.4 Jedno mesto za alate i ključeve
**Status: urađeno 12. oktobra 2026.** FFmpeg se svuda traži na isti način, Pixabay ključ je jedan za ceo program i može da se zameni i u Pixabay pretrazi, ostali ključevi idu kroz jedno šifrovano skladište. Ime Ollama modela ostaje na više mesta do prozora sa podešavanjima (2.1).
- Putanja do ffmpeg.exe je napisana na više od 40 mesta.
- Čuvanje API ključeva (šifrovano, što je dobro) ponavlja se u pet fajlova. Pixabay ključ uz to živi na dva mesta: AI kreator, Video Engine i Pixabay pretraga čitaju stari fajl, a prozor Medijski provajderi novi. Ključ promenjen u jednom prozoru zato ne važi u svim ostalim.
- Model za Ollamu je zapisan na više mesta.

Sve to treba da ide u jednu klasu (Tools / Settings / KeyStore). Tako će i podešavanja iz faze 2 imati jedno mesto odakle čitaju. Srednji posao.

### 1.5 Automatsko snimanje i oporavak
**Status: urađeno 12. oktobra 2026.**
Projekat bi se snimao na svaka 2 minuta u poseban fajl. Ako se program sruši, pri sledećem pokretanju bi ponudio vraćanje. Srednji posao.

### 1.6 Prenosivi projekti
**Status: urađeno 12. oktobra 2026.** Uz to, kad se otvori projekat sa nestalim fajlovima, program ponudi da ih potraži u folderu koji izabereš.
Putanje do medija se čuvaju kao pune putanje (C:\Users\...). Kad se projekat premesti na drugi disk ili računar, svi klipovi "nestanu". Treba čuvati putanje relativno u odnosu na projekat i dodati "Sakupi medije u folder projekta" i "Pronađi nestale fajlove". Srednji posao.

### 1.7 Detaljan log rendera
**Status: urađeno 12. oktobra 2026.** Komanda i kraj FFmpeg izlaza idu u Logs\render_last_error.log. U meniju Pomoć je nova stavka „Otvori folder sa log fajlovima“.
Kad render ne uspe, korisnik treba da dobije jasnu poruku, a pun FFmpeg izlaz da ode u log fajl koji može da pošalje. Mali posao.

**Ukupno za fazu 1: tri do četiri sesije, plus otprilike dve nedelje tvog povremenog testiranja. Kod je urađen 12. oktobra 2026; ostaje testiranje (1.1).**

## Faza 2: proizvod za druge ljude

Cilj: neko ko nije ti preuzme program, instalira ga i napravi prvi video bez tvoje pomoći.

### 2.1 Čarobnjak za prvo pokretanje i prozor sa podešavanjima
**Status (provereno 21. oktobra 2026): najveći deo već radi instaler** (installer/UltraVideoEditor.iss): bira jezik, sam preuzima FFmpeg, VLC i Ollama, prema grafičkoj kartici i RAM-u bira i preuzima modele (qwen2.5 14b/7b/3b, qwen2.5vl ili moondream), Whisper nudi kao opciju. U programu ostaje samo prozor sa podešavanjima, kao lepši dodatak.
Program zavisi od mnogo toga:
- FFmpeg;
- faster-whisper-xxl i modeli od 1,5 do 3 GB;
- Ollama sa modelom qwen2.5:14b (traži jaču grafičku karticu ili dosta RAM-a) i modelom za vid;
- API ključevi za Pixabay, Pexels, Coverr i Cloudflare.

Čarobnjak bi:
- proverio šta postoji;
- jasno rekao šta fali;
- ponudio preuzimanje direktno iz programa, bez slanja korisnika na sajtove;
- predložio manji model (qwen2.5:7b ili 3b) na slabijem računaru.

Prozor sa podešavanjima bi na jednom mestu imao:
- jezik i temu;
- folder za projekte;
- ključeve;
- modele;
- kvalitet rendera.

Veliki posao, najvažniji u ovoj fazi.

### 2.2 Veličina instalacije
**Status: urađeno u instaleru**, osim jedne stvari: zvučna biblioteka (Assets) nije u instaleru, a u tekstu release-a stoji „TODO: link ka Assets paketu“. Bez nje AI kreator nema ambijentalne zvuke kod drugih korisnika. Treba odlučiti gde paket stoji (GitHub release dozvoljava fajlove do 2 GB, pa biblioteku treba pretvoriti u OGG ili podeliti).
Zvučna biblioteka je Adobe-ova (besplatni zvučni efekti programa Audition); njihova licenca ne dozvoljava da se zvuci dele kao samostalan paket. Zato je (21. oktobra 2026) rešeno: instaler ima opcionu stavku, a program meni Pomoć → „Preuzmi biblioteku zvukova sa Adobe-a“; oba skidaju potrebne kategorije direktno sa Adobe-a i pretvaraju ih u OGG (Tools\DownloadAdobeSounds.ps1, oko 3,7 GB preuzimanja, oko 350 MB na disku). Samo zvučna biblioteka je veća od 4,8 GB, Whisper modeli su još oko 4,6 GB, a FFmpeg oko 230 MB. Instalacija od preko 10 GB odbija ljude. Predlog:
- osnovni instaler sa programom i FFmpeg-om;
- Whisper model koji se skida po potrebi;
- zvučna biblioteka u paketima (priroda, grad, deca, životinje...) koje korisnik bira.

Ako zvuci još nisu u OGG formatu, skripta ConvertAssetsToOgg koju već imaš može da ih smanji nekoliko puta. Srednji do veliki posao.

### 2.3 Instaler i potpis
**Status: instaler postoji i GitHub ga sam pravi na tag `video-v*`** (Inno Setup, prečice, deinstalacija). SHA-256 provera ažuriranja urađena 21. oktobra 2026 (program ne pokreće instaler čiji se otisak ne poklapa; release sada objavljuje i .sha256 fajl). Ostaje potpis programa (SmartScreen upozorenje; za projekte otvorenog koda postoji besplatan potpis preko SignPath Foundation, prijavljuješ se ti).
- Pravi instaler (Inno Setup ili MSIX), sa prečicom, deinstalacijom i izborom foldera.
- Potpisan program, inače Windows SmartScreen prikazuje upozorenje koje mnoge odbije. Sertifikat za potpis se plaća, pa vredi proveriti opcije pred izdanje.
- Ažuriranje treba da proveri SHA-256 otisak preuzetog .exe fajla pre pokretanja. Sada se preuzeti fajl pokreće bez provere.

Srednji posao.

### 2.4 Prevod do kraja
**Status: delimično urađeno 13. oktobra 2026.** Sve što je u tim delovima ostalo na srpskom (poruke koje čitač izgovara, napredak u Fazi 3, dijalozi, izveštaji, log) sada je prevedeno ili prati jezik programa. Engleski tekst u XAML-u ovih prozora (natpisi dugmadi i polja) još nije preveden na srpski i nemački.
Glavni prozor, AI kreator, YouTube dijalog i pregled scena su prevedeni na sva tri jezika. Bez prevoda, sa tekstom upisanim na engleskom (ponegde i na srpskom), ostali su:
- grupni izvoz, kolor grejding;
- Highlight i Faza 3;
- log, medijski provajderi, šabloni projekta;
- detekcija scena, Skia animacije, Timeline AI;
- Video Engine, pregled videa;
- pozicija slike, pomoć, pregled animacije.

Nazivi koje program sam daje klipovima ("Stih 3", "REFREN") treba da prate jezik programa. Veliki, ali mehanički posao.

### 2.5 Ime "Iskra"
Staro ime se pojavljuje 53 puta: "Iskra project", .iskra fajlovi, folder IskraExports, .iskraprovider. Brend je sada Ultra. Predlog: novi projekti dobijaju .ultra (ili ostaje .iskra kao interni format), stari .iskra fajlovi se i dalje otvaraju, a tekstovi i folderi prelaze na Ultra. Mali do srednji posao.

### 2.6 Licence i zasluge
**Status: urađeno 21. oktobra 2026.** THIRD-PARTY-NOTICES.md sa svim bibliotekama, alatima, AI modelima i servisima; instalira se uz program i otvara iz menija Pomoć. Napomena: mali model Qwen2.5 3B, koji instaler bira na slabijim računarima, nije za komercijalnu upotrebu.
- FFmpeg sa libx264 je pod GPL licencom. Pošto je i Ultra Video Editor pod GPLv3 i izvorni kod je javan, to je u redu. Treba samo dodati fajl sa licencama trećih strana (FFmpeg, LibVLC, Whisper, SkiaSharp, Magick.NET, NAudio...).
- **Zvučna biblioteka: ovo je najvažnije pre izdavanja pesme.** Komentar u kodu kaže da je biblioteka zamena za Freesound. Ako su zvuci sa Freesound-a, svaki ima svoju licencu. Zvuci pod CC-BY traže navođenje autora. Zvuci pod CC-BY-NC ne smeju u video koji zarađuje na YouTube-u. Pre nego što spot ode na monetizovan kanal, treba znati odakle su zvuci koji su u njemu.
- Pixabay, Pexels i Coverr imaju svoje uslove. Program bi pored svakog videa mogao da napravi fajl sa zaslugama (credits.txt): autori klipova i zvukova, spremno za opis na YouTube-u. To je i lepo prema autorima i štiti kanal.

Srednji posao.

### 2.7 Uputstvo
Pravo uputstvo na srpskom i engleskom, ugrađeno u program (F1 otvara deo koji se odnosi na prozor u kome si):
- deo za korisnike čitača ekrana: prečice, kako se kreće kroz tajmlajn, šta se najavljuje;
- deo za videće: miš, prozori, pregled.

Prozor sa pomoći (HelpWindow) je sada samo lista prečica na engleskom. Srednji posao.

### 2.8 Testiranje sa NVDA i Narratorom
Program je pravljen i testiran sa JAWS-om. Neko ko ga preuzme besplatno verovatno koristi NVDA. Jedan prolaz kroz sve prozore sa NVDA-om, plus provera alatom Accessibility Insights for Windows (besplatan, Microsoftov, prijavljuje kontrole bez imena), otkriće ono što JAWS sam popravlja u hodu. Na primer, u XAML-u desetak traka za napredak nema ime. Mali do srednji posao.

**Ukupno za fazu 2: šest do devet sesija, kalendarski mesec do dva.**

## Faza 3: veliki skok

Ovo je razlika između dobrog alata i ozbiljnog editora.

### 3.1 Pregled zvučnog miksa
**Status: urađeno kao R3.**
Danas se čuje samo jedan klip po jedan. Prvi korak je da se pesma, ambijent, efekti i govor čuju zajedno, sa jačinama, bez renderovanja slike. Za tebe kao slepog autora to je verovatno najkorisnija funkcija koja fali: čuješ da li je kiša pretiha pre nego što čekaš ceo render. Veliki posao.

### 3.2 Brzi pregled slike
Pregled celog tajmlajna, sa slikom, prelazima i titlovima, u niskoj rezoluciji (proxy). Tako rade svi ozbiljni editori. Može se praviti u pozadini dok korisnik radi i osvežavati samo deo koji se promenio. Veliki posao.

### 3.3 Grafički tajmlajn za videće
**Status: prva verzija urađena kao R7.**
Sada je tajmlajn lista. To je odlično za tastaturu i čitače ekrana, ali videći korisnik očekuje:
- trake sa klipovima;
- prevlačenje mišem;
- talasni oblik zvuka;
- sličice kadrova.

Važno: to bi bio drugi prikaz istog modela, ne drugi program. Lista ostaje za tastaturu, grafika za miš, a izmena u jednom se odmah vidi u drugom. To je stvarno "jednako za slepe i videće". Veliki posao, verovatno najveći u celom planu.

### 3.4 Razbijanje velikih fajlova
AIVideoCreator ima 7.577 linija, a MainWindow 5.987. Treba ih podeliti na celine:
- model tajmlajna;
- komande za izmene (to usput daje čistiji Undo/Redo);
- render plan;
- faze AI kreatora (tekst, režiser, pretraga, vid, ambijent, sklapanje) kao posebne klase;
- najave za čitač ekrana kao poseban servis.

Radi se postepeno, uz testove iz 1.2, da se ništa ne pokvari. Veliki posao, raspoređen kroz više sesija.

### 3.5 Brzina
- Skia animacije upisuju svaki kadr kao PNG na disk pre spajanja. Direktno slanje kadrova FFmpeg-u bilo bi višestruko brže, uz napredak i mogućnost otkazivanja (sada ih nema).
- Kod renderovanja vredi pamtiti već obrađene klipove, da se posle male izmene ne radi sve ispočetka.
- Kod AI kreatora vredi skidati više klipova odjednom.

Srednji posao.

**Ukupno za fazu 3: deset do petnaest sesija, kalendarski dva do četiri meseca.**

## Faza 4: ono na šta bi Adobe mogao da bude ljubomoran

Iskreno: Premiere Pro pravi stotine inženjera već više od trideset godina. U opštem montiranju (efekti, boje, profesionalni formati, saradnja timova) nijedan program jednog autora ga neće stići, i to nije ni potrebno. Ali u dve stvari Adobe je slab, a ti možeš biti najbolji na svetu:

1. Montiranje bez vida. Premiere je za slepog korisnika praktično neupotrebljiv.
2. Pravljenje spota za pesmu iz samog teksta i zvuka, na srpskom.

Ideje koje bi to učinile vidljivim:

### 4.1 Opisani tajmlajn
**Status: urađeno kao R4** (Ctrl+Shift+F, Ctrl+Alt+D, opisi prate glavu).
Dok se kreće kroz tajmlajn, AI kratko opisuje kadar na kome si: "devojčica trči kroz park, sunčano, krupni plan". Opisi se prave jednom, u pozadini, i pamte se. Nijedan profesionalni editor to nema. Veliki posao.

### 4.2 Audio-deskripcija za gledaoce
**Status: urađeno 21. oktobra 2026 (čeka test).** Meni AI → „Audio-deskripcija za gledaoce“: tekst opisa iz opisa kadrova (može da se ispravlja red po red), Google ili Windows glas, opisi u pauzama pevanja (prema titlovima), muzika se stiša ispod glasa; pored videa nastaju i .txt za YouTube i .vtt titl opisa.
Program iz opisa kadrova pravi dodatni audio-zapis sa opisom za slepe gledaoce, ili tekstualni opis za YouTube. Spot napravljen u Ultra editoru bi tako bio pristupačan i za slepu decu koja ga slušaju. To je jaka priča i za grant prijave. Veliki posao.

### 4.3 Glasovne komande
"Iseci ovde", "pomeri sledeću sliku na refren", "pusti od drugog stiha". Whisper već postoji u programu. Srednji do veliki posao.

### 4.4 Automatska provera pre objavljivanja
Pre izvoza program proveri:
- da li su titlovi čitljivi (kontrast, dužina);
- da li ima prebrzih bljeskova (opasno za fotosenzitivnu epilepsiju);
- da li je glasnoća po YouTube standardu;
- da li ima zvukova ili klipova sa licencom koja ne dozvoljava monetizaciju.

Srednji posao.

### 4.5 Zajednički rad
Ti i supruga radite na istom projektu: ona menja tekst, ti montiraš. Na primer preko deljenog foldera, sa zaključavanjem i istorijom izmena. Veliki posao, ima smisla tek posle faze 3.

**Ukupno za fazu 4: posao od nekoliko meseci, koji se može raditi deo po deo.**

## Procena vremena

Koliko bi meni trebalo:

| Faza | Moj rad | Kalendarski, uz tvoje testiranje |
|---|---|---|
| 0: brze ispravke | 1 sesija | nekoliko dana |
| 1: stabilnost | 3 do 4 sesije | 2 do 3 nedelje |
| 2: proizvod | 6 do 9 sesija | 1 do 2 meseca |
| 3: veliki skok | 10 do 15 sesija | 2 do 4 meseca |
| 4: jedinstvene funkcije | 10 do 20 sesija | 3 do 6 meseci, po delovima |

Ukupno je to od 30 do 50 sesija mog rada, a kalendarski od 8 do 14 meseci.

Zašto toliko, kad ja pišem kod brzo? Jer moj deo nije usko grlo. Ovde ne mogu da kompajliram ni da pokrenem program, pa svaka sesija zavisi od tvog build-a, testa i slušanja rezultata. Na kraju, samo ti možeš da kažeš da li spot zvuči i izgleda kako treba. Faze 0, 1 i 2 su sigurne procene. Faze 3 i 4 su grube, jer zavise od odluka koje još nismo doneli.

Moj savet je da ne ideš redom do kraja, nego ovako:

1. Faza 0 i 1, pre nego što spot za novu pesmu ode u javnost.
2. Iz faze 2: licence zvukova (2.6) odmah, a ostalo pre nego što program ponudiš drugim slepim korisnicima.
3. Iz faze 3, prvo pregled zvučnog miksa (3.1), jer najviše olakšava vaš rad.
4. Iz faze 4, opisani tajmlajn (4.1), jer je to funkcija po kojoj bi program postao poznat.
