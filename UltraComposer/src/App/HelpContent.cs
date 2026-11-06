using System.Collections.Generic;

namespace UltraComposer.App;

/// <summary>
/// Content for the "Uputstvo za upotrebu"/"User Guide" window (see
/// HelpWindow) - end-user, "what do I press and what happens" instructions,
/// as opposed to README.md's more architecture-flavored explanations for
/// developers. Kept as its own file rather than folded into Localization.cs
/// because these are long, topic-sized blocks rather than short UI labels.
///
/// Every button/menu name quoted below is the exact wording it has in the
/// currently-selected language, so keep this in sync whenever a control's
/// label text changes - a topic that quotes a button name that no longer
/// exists is worse than no help at all.
/// </summary>
public static class HelpContent
{
    public sealed class Topic
    {
        public string Title { get; }
        public string Body { get; }

        public Topic(string title, string body)
        {
            Title = title;
            Body = body;
        }
    }

    public static List<Topic> GetTopics()
    {
        return Localization.Current == AppLanguage.Serbian ? Sr() : En();
    }

    private static List<Topic> Sr() => new()
    {
        new Topic(
            "Osnove i navigacija",
            "Ultra Composer ima šest glavnih prikaza, dostupnih iz menija \"Prikaz\": Banka " +
            "instrumenata, Sekvenser, Aranžer, Multi-trake i mikser, Auto-pratnja i Zvuk i efekti. U datom " +
            "trenutku vidljiv je samo jedan, ali stanje koje deluju - učitani instrument u " +
            "\"Banci instrumenata\", povezani MIDI uređaj i slično - deljeno je između svih " +
            "prikaza.\n\n" +
            "Kroz ceo program se krećeš normalnim Tab/Shift+Tab redosledom, kao kroz bilo koji " +
            "Windows program. Svaka bitna promena stanja (nota dodata, puštanje pokrenuto ili " +
            "zaustavljeno, MIDI povezan, akord uhvaćen...) automatski se najavljuje kroz JAWS - " +
            "nije potrebno ručno tražiti šta se promenilo.\n\n" +
            "Jezik programa (srpski/engleski) menjaš u meniju \"Jezik\" - promena je trenutna, " +
            "bez potrebe za restartom programa. Ovo uputstvo prikazuje sadržaj na jeziku koji je " +
            "bio aktivan u trenutku kad si ga otvorio/la."),

        new Topic(
            "Klavijatura za sviranje i MIDI",
            "U \"Banci instrumenata\" se nalazi oblast \"Klavijatura za sviranje\" - do nje dolaziš " +
            "tasterom Tab. Samo dok je ta oblast fokusirana, pritisci tastera na tastaturi " +
            "postaju note; svuda drugde u programu tastatura radi normalno.\n\n" +
            "Raspored (isti kao FL Studio i većina trekera):\n" +
            "- Donji red (jedna oktava): Z S X D C V G B H N J M ,\n" +
            "- Gornji red (sledeća oktava naviše): Q 2 W 3 E R 5 T 6 Y 7 U I\n" +
            "- Page Up / Page Down: pomera osnovnu oktavu naviše/naniže\n" +
            "- Strelica gore/dole: menja instrument u učitanoj SoundFont banci, a kad banka nije " +
            "učitana, zvuk Ultra FM sintisajzera\n" +
            "- Taster levo od 1 / taster 1: pitch bend - dok držiš, ton klizi naniže/naviše, a kad pustiš, vraća se\n" +
            "- Razmak: sustain pedala (dok ga držiš, tonovi zvone i kad pustiš tastere)\n" +
            "- F2/F3: modulacioni točkić (vibrato) manje/više, F4/F5: transpozicija pola stepena dole/gore\n" +
            "- F6: arpeđator, F7: portamento, F8: poli/mono/legato, F9: svi efekti, Home: zaustavi sve tonove\n\n" +
            "Za pravi MIDI instrument (npr. Yamaha P-125 ili bilo koji drugi USB-MIDI uređaj): " +
            "izaberi uređaj iz padajuće liste \"MIDI uređaj\", klikni \"Osveži uređaje\" ako se ne " +
            "vidi odmah, pa \"Poveži\". Kad je povezan, sviranje sa te tastature radi potpuno " +
            "isto kao i sa računarske tastature."),

        new Topic(
            "Zvuk i efekti: Ultra FM, pitch bend, portamento, legato, arpeđator",
            "Novo u verziji 2.0. Meni \"Prikaz\", \"Zvuk i efekti\". Ovde podešavaš zvuk klavijature za " +
            "sviranje, kao na pravim klavijaturama. Ekran ima dve liste: \"Odeljci\" (Alt+O) i \"Podešavanja\" " +
            "(Alt+P). U listi odeljaka strelicama biraš odeljak, a razmak uključuje ili isključuje efekat ili " +
            "arpeđator. Tab ili Enter vodi u podešavanja tog odeljka. Tamo:\n" +
            "- strelice gore/dole: sledeće podešavanje\n" +
            "- strelice levo/desno: menja vrednost (JAWS odmah izgovori novu vrednost)\n" +
            "- Page Up / Page Down: menja u većim koracima\n" +
            "- Home / End: najmanja / najveća vrednost\n" +
            "- razmak ili Enter: uključi/isključi, ili sledeći izbor\n" +
            "- Delete: početna vrednost, Escape: nazad na odeljke\n\n" +
            "ULTRA FM SINTISAJZER (kao FM8): šest operatora (sinusnih oscilatora). Algoritam određuje koji " +
            "se operatori čuju (nosioci), a koji menjaju boju tona drugima (modulatori) - naziv odeljka svakog " +
            "operatora to kaže. Svaki operator ima jačinu, odnos frekvencije, fino štimovanje, ADSR omotač " +
            "(napad, opadanje, zadržavanje, otpuštanje) i osetljivost na jačinu udara. Šesnaest gotovih " +
            "zvukova: električni klavir, zvono, FM bas, duvači, orgulje, pad, solo, gudači, marimba, klavinet, " +
            "flauta, staklo, trzani bas, hor, steel drum i čist ton. \"Sjaj\" jednim potezom posvetli ili " +
            "potamni ceo zvuk. Ultra FM se čuje kad u Sloju 1 nije učitana SoundFont banka ni VST (dugme " +
            "\"Isključi banku\" u Banci instrumenata); u slojevima 2-4 ga biraš kao \"ugrađeni sintisajzer\".\n\n" +
            "IZVOĐENJE: pitch bend (opseg do 24 polutona), modulacioni točkić za vibrato (dubina i brzina), " +
            "portamento (klizanje sa tona na ton, sa podesivim vremenom), način sviranja poli / mono / legato " +
            "(legato: novi ton se veže na prethodni bez novog napada, a kad pustiš gornji, vraća se na onaj koji " +
            "još držiš), transpozicija i sustain pedala. Sve ovo radi i sa MIDI klavijature (pitch bend i " +
            "modulacioni točkić, sustain pedala, CC 5 i 65 za portamento). Sa SoundFont bankom pitch bend, " +
            "vibrato, mono i legato rade direktno; portamento radi u mono i legato načinu. VST instrument dobija " +
            "pitch bend i modulacioni točkić, a vibrato i klizanje podešavaš u samom VST-u.\n\n" +
            "ARPEĐATOR: drži akord, a arpeđator ga svira ton po ton - naviše, naniže, naviše pa naniže, " +
            "nasumično, redom kako sviraš ili ceo akord u ritmu. Biraš brzinu (od četvrtina do tridesetdvojki), " +
            "opseg u oktavama, dužinu tona, tempo i \"zadrži\" (latch: svira i kad pustiš tastere, dok ne " +
            "odsviraš novi akord).\n\n" +
            "EFEKTI (redom kojim zvuk prolazi): distorzija, filter i vau-vau, ekvilajzer, kompresor, korus, " +
            "flanger, fejzer, tremolo, auto-pan, rotacioni (Lesli) zvučnik, eho i reverb. \"Brzo podešavanje\" " +
            "u odeljku Efekti odmah postavlja gotovu kombinaciju (koncertna sala, orgulje sa Leslijem, rok solo, " +
            "svemirski pad, vau-vau, stari radio...). Efekti deluju na sve što sviraš na klavijaturi (slojevi " +
            "1-4, bilo koji instrument), a ne na multi-trake i auto-pratnju.\n\n" +
            "Sva ova podešavanja se čuvaju u projektu. Dugme \"Zaustavi sve tonove\" (ili Home na klavijaturi) " +
            "prekida sve tonove i vraća pitch bend i modulaciju na nulu."),

        new Topic(
            "SoundFont i VST3 instrumenti",
            "Zajednički instrument koji se koristi u \"Banci instrumenata\" (i time i u " +
            "Sekvenseru i Aranžeru) može biti: ugrađeni sintisajzer (uvek dostupan, bez podešavanja), " +
            "SoundFont banka (.sf2 fajl), ili VST3 dodatak (.vst3 fajl/fascikla) - poslednja dva se " +
            "mogu koristiti i istovremeno sa ugrađenim sintisajzerom.\n\n" +
            "SoundFont: ukucaj ili nalepi putanju do .sf2 fajla u polje, ili je pronađi dugmetom " +
            "\"Pretraži...\", pa klikni \"Učitaj banku\". Odmah nakon toga bira se instrument iz " +
            "padajuće liste. Program dolazi sa već ugrađenom podrazumevanom bankom (GeneralUser GS) " +
            "koja radi bez ikakvog podešavanja. \"Isključi banku\" vraća na ugrađeni sintisajzer.\n\n" +
            "VST3: isti princip - putanja/Pretraži, pa \"Učitaj instrument\"; \"Isključi instrument\" " +
            "ga isključuje.\n\n" +
            "Svaka traka u Multi-trakama, i svaki sloj (bas/kontra/harmonija) u Auto-pratnji, može " +
            "učitati SVOJU sopstvenu, nezavisnu SoundFont banku ili VST3 - tako svaki može zvučati " +
            "kao potpuno drugi instrument u isto vreme."),

        new Topic(
            "Automatska terca",
            "Brz način da dobiješ dvoglasje bez druge ruke ili druge note: u \"Banci instrumenata\", " +
            "čekiraj \"Uključi automatsku tercu\" i izaberi \"Gornja terca\" ili \"Donja terca\".\n\n" +
            "Dok je uključeno, SVAKA nota odsvirana bilo gde u programu (tastatura, MIDI, Sekvenser, " +
            "Aranžer) automatski dobija i drugu notu, tercu iznad ili ispod, u isto vreme.\n\n" +
            "Terca prati pravu lestvicu, a ne uvek isti razmak: izaberi osnovni ton i lestvicu " +
            "(Dur, Mol, ili orijentalne Hidžaz/Hidžaz kar) - terca se računa prema tome kojoj " +
            "lestvici ta nota pripada, isto kao u pravoj muzičkoj teoriji. Nota koja ne pripada " +
            "izabranoj lestvici i dalje dobija običnu veliku tercu (4 polustepena)."),

        new Topic(
            "Sekvenser",
            "Sekvenser je spisak nota koje se ponavljaju u petlji - ne mreža koju bi morao/la mišem " +
            "da klikaš, nego lista koju čitaš i menjaš normalno kroz JAWS.\n\n" +
            "Dodavanje note ili akorda: ukucaj ime note (solfeđo c/cis/d/dis-es/e/f/fis/g/gis-as/" +
            "a/ais-b/h sa brojem oktave, npr. c4, ili englesko ime kao C#4), podesi početak " +
            "(otkucaj), trajanje i jačinu, pa klikni \"Dodaj notu/akord\". Za akord, ukucaj više " +
            "nota razdvojenih zapetom (npr. c4, e4, g4) - sve se dodaju odjednom, u isto vreme.\n\n" +
            "\"Slušaj akord\": klikni da naoružaš hvatanje akorda, odsviraj notu ili akord " +
            "(tastatura ili MIDI, jednu ili više nota odjednom), pusti tastere - odsvirano se " +
            "dodaje kao jedan akord, sa tačno onim jačinama kojima si svirao/la, a polje za " +
            "početni otkucaj se samo pomera za trenutno trajanje, spremno za sledeći akord. " +
            "\"Prestani da slušaš\" isključuje hvatanje. Samo jedan od Sekvensera/Multi-traka može " +
            "biti naoružan u isto vreme.\n\n" +
            "\"Ukloni izabranu notu\"/\"Obriši sve note\" brišu iz spiska. \"Sviraj\"/\"Zaustavi\" " +
            "puštaju petlju (podesi tempo i dužinu petlje iznad).\n\n" +
            "\"Prikaži piano roll\" je čisto vizuelni, opcioni dodatak spisku (za saradnika koji " +
            "vidi) - levi klik dodaje notu na mreži, desni klik je briše. Nije potreban za rad " +
            "kroz JAWS, sam spisak nota je pravi način rada."),

        new Topic(
            "Aranžer",
            "Aranžer nižе sačuvane obrasce (snimke Sekvensera) u jedan uređeni raspored - " +
            "npr. strofa, strofa, refren, strofa...\n\n" +
            "1. Napravi obrazac u Sekvenseru kao i obično.\n" +
            "2. U Aranžeru, ukucaj ime i klikni \"Sačuvaj trenutni sekvenser kao obrazac\" - ovo " +
            "pravi snimak trenutnih nota i dužine petlje Sekvensera pod tim imenom.\n" +
            "3. Izaberi sačuvani obrazac i klikni \"Dodaj u redosled\" da ga dodaš na kraj rasporeda " +
            "(isti obrazac može da se doda više puta - npr. strofa, strofa, refren).\n" +
            "4. Klikni \"Sviraj aranžman\" - pušta ceo raspored od početka, ponavlja ceo aranžman " +
            "kad stigne do kraja; status linija najavljuje koji obrazac trenutno svira.\n" +
            "5. \"Ukloni poslednji iz redosleda\"/\"Obriši ceo redosled\" menjaju raspored; " +
            "\"Ukloni izabrani obrazac\" briše sačuvani obrazac u potpunosti."),

        new Topic(
            "Multi-trake i mikser",
            "Više nezavisnih traka, svaka sa sopstvenim notama i sopstvenim instrumentom, koje " +
            "sviraju zajedno kao jedna pesma.\n\n" +
            "\"Dodaj traku\" pravi novu traku. Izaberi je iz liste da uređuješ u panelu \"Izabrana " +
            "traka\": \"Preimenuj\" menja ime; instrument bira se preko tri opcije - \"Ugrađeni " +
            "sintisajzer\", \"SoundFont banka (posebna za ovu traku)\" ili \"VST3 (poseban za ovu " +
            "traku)\", svaki sa sopstvenim, nezavisnim učitavanjem; \"Primeni jačinu i pan\" " +
            "postavlja glasnoću i levo/desno; \"Isključi zvuk (mute)\" i \"Solo (samo ova traka)\" " +
            "kontrolišu čujnost.\n\n" +
            "Note se unose potpuno isto kao u Sekvenseru (\"Dodaj notu/akord\", \"Slušaj akord\", " +
            "\"Ukloni izabranu notu\", \"Obriši sve note\") - svaka traka ima svoj sopstveni spisak.\n\n" +
            "Tempo i dužina petlje su zajednički za sve trake (sviraju jednu pesmu zajedno) - " +
            "\"Sviraj sve trake\"/\"Zaustavi\" pokreću/zaustavljaju sve odjednom.\n\n" +
            "\"Izvezi kao WAV...\" snima trenutni miks (poštujući jačinu/pan/mute/solo svake trake) " +
            "u običan .wav fajl - npr. da bi ga dalje mešao/la u Ultra Audio Editor-u sa vokalom."),

        new Topic(
            "Auto-pratnja",
            "Ugrađena pratnja u stilu kućne klavijature - izabereš ritam i on sam svira bubnjeve, " +
            "bas, kontru i harmoniju, prateći akord koji trenutno sviraš.\n\n" +
            "Izaberi ritam iz liste \"Ritam\" (Pop, Rok, Balada, Valcer, Latino, Sving, Regi, Fanki, " +
            "Bluz, Orijentalni, plus bilo koji sopstveni ritam koji si sačuvao/la - vidi dole). " +
            "Čekiraj/isčekiraj \"Bubnjevi\", \"Bas\", \"Kontra\", \"Harmonija\" da uključiš/isključiš " +
            "svaki sloj posebno, čak i dok svira.\n\n" +
            "Unos akorda: \"Automatski (sviraj akord levom rukom)\" - sviraj akord ispod granice " +
            "podele (podesi je i klikni \"Primeni\"), a melodiju iznad; ili \"Ručno (biram akord)\" - " +
            "izaberi osnovni ton i vrstu akorda, klikni \"Primeni akord\".\n\n" +
            "\"Primeni tempo\", pa \"Sviraj pratnju\"/\"Zaustavi\" - pratnja radi nezavisno od " +
            "Sekvensera/Aranžera/Multi-traka, ne zaustavlja ih i one ne zaustavljaju nju.\n\n" +
            "Instrument po sloju: pod \"Instrumenti pratnje\", izaberi Bas/Kontra/Harmonija (ili " +
            "\"Komplet\" za sve tri odjednom) iz liste \"Sloj\", pa učitaj sopstvenu SoundFont banku " +
            "ili VST3 samo za taj sloj (\"Učitaj banku za ovaj sloj\"/\"Učitaj VST za ovaj sloj\").\n\n" +
            "Sopstveni ritam (\"Sopstveni ritam\", niže na istom prikazu): gradiš sopstveni ritam " +
            "takt po takt. Dodaj udarce bubnja (\"Dodaj udarac\") i/ili melodijske udarce " +
            "bas/kontra/harmonija (\"Dodaj\"), pa klikni \"Dodaj kao novi takt\" - takt se dodaje na " +
            "kraj ritma, a editor se čisti za sledeći. Kad ritam zvuči kako želiš, ukucaj ime i " +
            "klikni \"Sačuvaj ritam\" - pojavljuje se u listi \"Ritam\" odmah pored ugrađenih. " +
            "\"Novi ritam\" čisti editor za potpuno nov ritam. Svaki već sačuvan ritam može se " +
            "kasnije ponovo urediti (\"Uredi izabrani\") ili obrisati (\"Ukloni izabrani ritam\")."),

        new Topic(
            "Čuvanje i otvaranje projekta",
            "Meni \"Fajl\": \"Sačuvaj projekat\"/\"Sačuvaj kao...\" upisuje BAŠ SVE što se trenutno " +
            "dešava u projektu - note u Sekvenseru, sve trake i njihove note/instrumente/jačinu/pan, " +
            "sve sačuvane obrasce i redosled u Aranžeru, kompletno stanje Auto-pratnje (izabrani " +
            "ritam, tempo, uključeni slojevi, način unosa akorda, instrumenti po sloju, svi sačuvani " +
            "sopstveni ritmovi sa svojim taktovima), i podešavanje automatske terce - u jedan " +
            "\".adem\" fajl.\n\n" +
            "\"Otvori projekat...\" vraća sve tačno onako kako je ostavljeno, uključujući sopstvene " +
            "ritmove i dalje spremne za uređivanje (ne samo gotov zvuk)."),

        new Topic(
            "Promena jezika",
            "Meni \"Jezik\" (ili \"Language\" kad je program na engleskom) - izaberi \"Srpski\" ili " +
            "\"Engleski\"/\"English\". Svaka oznaka, meni i status poruka se menjaju odmah, bez " +
            "potrebe za ponovnim pokretanjem programa. Ovo uputstvo takođe prati taj izbor, ali " +
            "samo u trenutku kad ga otvoriš - ako promeniš jezik dok je uputstvo već otvoreno, " +
            "zatvori ga i ponovo otvori da vidiš novi jezik ovde."),

        new Topic(
            "Vizuelni interfejs i klavijatura na ekranu",
            "Ultra Composer nudi pregledan vizuelni interfejs sa tamnom temom visokog kontrasta " +
            "(u skladu sa standardima pristupačnosti), jasnim naglašenim bojama po oblastima rada " +
            "i bočnom trakom za navigaciju:\n\n" +
            "- Bočna navigacija (leva strana): brzi prelazak između prikaza (Banka instrumenata, " +
            "Sekvenser, Aranžer, Multi-trake i mikser, Auto-pratnja, Zvuk i efekti) klikom miša " +
            "ili tastaturom.\n" +
            "- Klavijatura na ekranu: na dnu prozora prikazana je klavijatura od 5 oktava (C2 do C7) " +
            "sa dirkama koje svetle kada ton svira iz bilo kog izvora (računarska tastatura, miš " +
            "ili povezani MIDI uređaj). Klikom miša na dirke direktno sviraš note. U meniju \"Prikaz\" " +
            "opcijom \"Prikaži klavijaturu na ekranu\" možeš je uključiti ili isključiti. Klavijatura " +
            "je napravljena tako da ne ometa JAWS navigaciju i ne ulazi u Tab redosled.\n" +
            "- Sviranje nota svuda sa tastature: podrazumevano je uključena opcija \"Sviraj note " +
            "računarskom tastaturom svuda\" (u meniju \"Prikaz\" i u polju klavijature za sviranje). " +
            "To omogućava da note sviraš tasterima Z-M i Q-U iz bilo kog dela programa, bez potrebe " +
            "da prethodno Tab-om fokusiraš oblast za sviranje. Note se automatski ne sviraju dok " +
            "kucaš tekst u poljima za unos, niti dok držiš tastere Ctrl ili Alt za prečice i menije. " +
            "Korisnici čitača ekrana ovu opciju mogu u svakom trenutku isključiti ako žele da note " +
            "sviraju isključivo dok je polje za sviranje fokusirano."),
    };

    private static List<Topic> En() => new()
    {
        new Topic(
            "Getting Started & Navigation",
            "Ultra Composer has six main views, reachable from the \"View\" menu: Instrument " +
            "Bank, Sequencer, Arranger, Multi-track & Mixer, Auto-accompaniment, and Sound and effects. Only one is " +
            "visible at a time, but shared state - the loaded instrument in \"Instrument Bank\", " +
            "the connected MIDI device, and so on - carries across every view.\n\n" +
            "You move through the whole app with the normal Tab/Shift+Tab order, like any Windows " +
            "program. Every meaningful change (a note added, playback started or stopped, MIDI " +
            "connected, a chord captured...) is announced automatically through JAWS - there's no " +
            "need to hunt for what changed.\n\n" +
            "The app's language (Serbian/English) is switched from the \"Language\" menu - the " +
            "switch is instant, no restart needed. This guide shows whichever language was active " +
            "the moment you opened it."),

        new Topic(
            "Performance Keyboard & MIDI",
            "\"Instrument Bank\" has a \"Performance keyboard\" region, reached with Tab. Only " +
            "while that region has focus do key presses turn into notes; everywhere else in the " +
            "app the keyboard works normally.\n\n" +
            "Layout (same as FL Studio and most trackers):\n" +
            "- Lower row (one octave): Z S X D C V G B H N J M ,\n" +
            "- Upper row (next octave up): Q 2 W 3 E R 5 T 6 Y 7 U I\n" +
            "- Page Up / Page Down: shift the base octave up/down\n" +
            "- Up/Down arrow: cycle the instrument in the loaded SoundFont bank, or the Ultra FM " +
            "synthesizer sound when no bank is loaded\n" +
            "- Key left of 1 / key 1: pitch bend - the note slides down/up while you hold, and comes back when you let go\n" +
            "- Space: sustain pedal (while held, notes keep ringing after you release the keys)\n" +
            "- F2/F3: modulation wheel (vibrato) less/more, F4/F5: transpose a semitone down/up\n" +
            "- F6: arpeggiator, F7: portamento, F8: poly/mono/legato, F9: all effects, Home: stop all notes\n\n" +
            "For a real MIDI instrument (e.g. a Yamaha P-125 or any other class-compliant USB-MIDI " +
            "device): pick it from the \"MIDI device\" dropdown, click \"Refresh devices\" if it " +
            "doesn't show up right away, then \"Connect\". Once connected, playing from it works " +
            "exactly like the computer keyboard."),

        new Topic(
            "Sound and effects: Ultra FM, pitch bend, portamento, legato, arpeggiator",
            "New in version 2.0. Menu \"View\", \"Sound and effects\". This is where you shape the sound of " +
            "the performance keyboard, like on a real keyboard. The screen has two lists: \"Sections\" (Alt+S) " +
            "and \"Settings\" (Alt+T). In the section list the arrows pick a section and Space switches an " +
            "effect or the arpeggiator on or off. Tab or Enter takes you to that section's settings. There:\n" +
            "- Up/Down arrows: next setting\n" +
            "- Left/Right arrows: change the value (JAWS says the new value right away)\n" +
            "- Page Up / Page Down: bigger steps\n" +
            "- Home / End: lowest / highest value\n" +
            "- Space or Enter: switch on/off, or the next choice\n" +
            "- Delete: starting value, Escape: back to the sections\n\n" +
            "ULTRA FM SYNTHESIZER (like FM8): six operators (sine oscillators). The algorithm decides which " +
            "operators are heard (carriers) and which ones shape the tone of others (modulators) - each " +
            "operator's section name tells you. Every operator has a level, frequency ratio, fine detune, an " +
            "ADSR envelope (attack, decay, sustain, release) and velocity sensitivity. Sixteen ready sounds: " +
            "electric piano, bell, FM bass, brass, organ, pad, lead, strings, marimba, clavinet, flute, glass, " +
            "pluck bass, choir, steel drum and pure sine. \"Brightness\" makes the whole sound brighter or " +
            "darker in one move. Ultra FM is heard when no SoundFont bank or VST is loaded in Layer 1 (the " +
            "\"Unload bank\" button in the Instrument Bank); in layers 2-4 pick it as \"built-in synth\".\n\n" +
            "PERFORMANCE: pitch bend (range up to 24 semitones), modulation wheel for vibrato (depth and " +
            "speed), portamento (gliding from note to note, adjustable time), poly / mono / legato voice mode " +
            "(legato: a new note connects to the previous one without a new attack, and when you release the " +
            "upper one it goes back to the one you still hold), transpose and the sustain pedal. All of this " +
            "also works from a MIDI keyboard (pitch bend and modulation wheels, sustain pedal, CC 5 and 65 for " +
            "portamento). With a SoundFont bank, pitch bend, vibrato, mono and legato work directly; portamento " +
            "works in mono and legato mode. A VST instrument gets the pitch bend and modulation wheels, and you " +
            "set its vibrato and glide in the VST itself.\n\n" +
            "ARPEGGIATOR: hold a chord and the arpeggiator plays it note by note - up, down, up and down, " +
            "random, as played, or the whole chord in rhythm. You choose the rate (quarter notes to " +
            "thirty-second notes), the range in octaves, the note length, the tempo and latch (keeps playing " +
            "after you release the keys, until you play a new chord).\n\n" +
            "EFFECTS (in the order the sound goes through them): overdrive, filter and auto-wah, equalizer, " +
            "compressor, chorus, flanger, phaser, tremolo, auto-pan, rotary (Leslie) speaker, echo and reverb. " +
            "\"Quick preset\" in the Effects section sets a ready combination at once (concert hall, organ " +
            "with rotary speaker, rock solo, space pad, auto-wah, old radio...). The effects work on everything " +
            "you play on the keyboard (layers 1-4, any instrument), not on the multi-track and the " +
            "auto-accompaniment.\n\n" +
            "All of these settings are saved in the project. The \"Stop all notes\" button (or Home on the " +
            "keyboard) stops every note and centres pitch bend and modulation."),

        new Topic(
            "SoundFont & VST3 Instruments",
            "The shared instrument used in \"Instrument Bank\" (and therefore in the Sequencer and " +
            "Arranger too) can be: the built-in synth (always available, no setup), a SoundFont " +
            "bank (.sf2 file), or a VST3 plug-in (.vst3 file/folder) - the last two can also play " +
            "at the same time as the built-in synth.\n\n" +
            "SoundFont: type or paste the path to a .sf2 file, or find it with \"Browse...\", then " +
            "click \"Load bank\" - an instrument dropdown appears right after. The app ships with a " +
            "default bank (GeneralUser GS) already bundled in, working with zero setup. \"Unload " +
            "bank\" switches back to the built-in synth.\n\n" +
            "VST3: same idea - path/Browse, then \"Load instrument\"; \"Unload instrument\" turns " +
            "it off.\n\n" +
            "Every track in Multi-track, and every layer (bass/kontra/harmony) in Auto-accompaniment, " +
            "can load its OWN independent SoundFont bank or VST3 - so each can sound like a " +
            "completely different real instrument at the same time."),

        new Topic(
            "Auto-Third Harmonization",
            "A quick way to get two-part harmony without a second hand or a second note: in " +
            "\"Instrument Bank\", check \"Enable auto-third\" and pick a third above or a third " +
            "below.\n\n" +
            "While it's on, EVERY note played anywhere in the app (keyboard, MIDI, Sequencer, " +
            "Arranger) automatically triggers a second note, a third away, at the same time.\n\n" +
            "The third follows a real scale rather than always the same gap: pick a root note and " +
            "a scale (Major, Minor, or the oriental-flavored Hijaz/Hijaz Kar) - the interval is " +
            "worked out from which scale degree the note actually is, the same way real harmony " +
            "works. A note outside the chosen scale still gets a plain major third."),

        new Topic(
            "Sequencer",
            "The Sequencer is a looping list of notes - not a grid you'd need a mouse to click on, " +
            "but a list you read and edit normally through JAWS.\n\n" +
            "Adding a note or chord: type a note name (English like C#4, or the solfège spelling " +
            "c/cis/d/dis-es/e/f/fis/g/gis-as/a/ais-b/h with an octave number, e.g. c4), set its " +
            "start beat, length and velocity, then click \"Add note/chord\". For a chord, type " +
            "several notes separated by commas (e.g. c4, e4, g4) - they're all added at once, at " +
            "the same time.\n\n" +
            "\"Listen for chord\": click to arm it, play a note or chord (keyboard or MIDI, one or " +
            "more keys at once), let go - what you played is inserted as one chord, using the " +
            "exact velocities you played it with, and the start-beat field advances by the current " +
            "length automatically, ready for the next chord. \"Stop listening\" disarms it. Only " +
            "one of the Sequencer/Multi-track views can be armed at a time.\n\n" +
            "\"Remove selected note\"/\"Clear all notes\" remove from the list. \"Play\"/\"Stop\" " +
            "control loop playback (set tempo and loop length above).\n\n" +
            "\"Show piano roll\" is a purely visual, optional addition to the list (for a sighted " +
            "collaborator) - left-click adds a note on the grid, right-click removes one. It's not " +
            "needed for working through JAWS; the note list itself is the real way to work."),

        new Topic(
            "Arranger",
            "The Arranger chains saved patterns (Sequencer snapshots) into one ordered arrangement - " +
            "e.g. verse, verse, chorus, verse...\n\n" +
            "1. Build a pattern in the Sequencer as usual.\n" +
            "2. In the Arranger, type a name and click \"Save current sequencer as pattern\" - this " +
            "snapshots the Sequencer's current notes and loop length under that name.\n" +
            "3. Select a saved pattern and click \"Append to order\" to add it to the end of the " +
            "play order (the same pattern can be added more than once - e.g. verse, verse, chorus).\n" +
            "4. Click \"Play arrangement\" - plays the whole order from the top, looping the whole " +
            "arrangement once it reaches the end; a status line reports which pattern is currently " +
            "playing.\n" +
            "5. \"Remove last from order\"/\"Clear whole order\" edit the play order; \"Remove " +
            "selected pattern\" deletes a saved pattern entirely."),

        new Topic(
            "Multi-track & Mixer",
            "Several independent tracks, each with its own notes and its own instrument, playing " +
            "together as one song.\n\n" +
            "\"Add track\" creates a new track. Select it from the list to edit it in the " +
            "\"Selected track\" panel: \"Rename\" changes its name; the instrument is one of three " +
            "options - \"Built-in synth\", \"SoundFont bank (dedicated to this track)\" or \"VST3 " +
            "(dedicated to this track)\", each with its own independent load; \"Apply volume and " +
            "pan\" sets loudness and left/right; \"Mute\" and \"Solo (this track only)\" control " +
            "what's audible.\n\n" +
            "Notes are entered exactly like the Sequencer (\"Add note/chord\", \"Listen for chord\", " +
            "\"Remove selected note\", \"Clear all notes\") - each track keeps its own list.\n\n" +
            "Tempo and loop length are shared across every track (they're playing one song together) - " +
            "\"Play all tracks\"/\"Stop\" start/stop every track's transport together.\n\n" +
            "\"Export as WAV...\" renders the current mix (respecting every track's volume/pan/mute/" +
            "solo) to a plain .wav file - e.g. to bring into Ultra Audio Editor for further mixing " +
            "with a vocal."),

        new Topic(
            "Auto-Accompaniment",
            "A built-in backing band, like a home keyboard's auto-accompaniment - pick a style and " +
            "it plays drums, bass, kontra and harmony on its own, following whatever chord you're " +
            "currently playing.\n\n" +
            "Pick a rhythm from the \"Rhythm\" list (Pop, Rock, Ballad, Waltz, Latin, Swing, Reggae, " +
            "Funk, Blues, Oriental, plus any custom rhythm you've saved - see below). Check/uncheck " +
            "\"Drums\", \"Bass\", \"Kontra\", \"Harmony\" to turn each layer on or off independently, " +
            "even while it's playing.\n\n" +
            "Chord input: \"Automatic (play the chord with your left hand)\" - play a chord below " +
            "the split point (set it and click \"Apply\"), and a melody above it; or \"Manual " +
            "(I pick the chord)\" - pick a root note and chord type, click \"Apply chord\".\n\n" +
            "\"Apply tempo\", then \"Play accompaniment\"/\"Stop\" - the accompaniment runs " +
            "independently of the Sequencer/Arranger/Multi-track, it doesn't stop them and they " +
            "don't stop it.\n\n" +
            "Instrument per layer: under \"Accompaniment instruments\", pick Bass/Kontra/Harmony " +
            "(or \"Kit\" for all three at once) from the \"Layer\" list, then load a SoundFont bank " +
            "or VST3 just for that layer (\"Load bank for this layer\"/\"Load VST for this layer\").\n\n" +
            "Custom rhythm (\"Custom rhythm\", further down the same view): build your own rhythm " +
            "one bar at a time. Add drum hits (\"Add hit\") and/or bass/kontra/harmony hits " +
            "(\"Add\"), then click \"Add as new bar\" - the bar is appended to the rhythm and the " +
            "editor clears for the next one. Once it sounds right, type a name and click \"Save " +
            "rhythm\" - it appears in the \"Rhythm\" list right next to the built-in ones. \"New " +
            "rhythm\" clears the editor to start a completely different one. Any already-saved " +
            "rhythm can later be reopened for editing (\"Edit selected\") or deleted (\"Remove " +
            "selected rhythm\")."),

        new Topic(
            "Saving & Opening a Project",
            "\"File\" menu: \"Save project\"/\"Save as...\" writes down EVERYTHING currently " +
            "happening in the project - the Sequencer's notes, every track and its notes/" +
            "instrument/volume/pan, every saved pattern and the play order in the Arranger, the " +
            "complete Auto-accompaniment state (selected rhythm, tempo, which layers are on, chord " +
            "input mode, per-layer instruments, every saved custom rhythm with its own bars), and " +
            "the auto-third setting - into one \".adem\" file.\n\n" +
            "\"Open project...\" brings all of that back exactly as it was left, including custom " +
            "rhythms still ready for further editing (not just the finished sound)."),

        new Topic(
            "Changing the Language",
            "The \"Language\" menu (or \"Jezik\" when the app is in Serbian) - pick \"English\" or " +
            "\"Serbian\"/\"Srpski\". Every label, menu and status message switches immediately, no " +
            "restart needed. This guide follows that same choice, but only at the moment you open " +
            "it - if you switch language while the guide is already open, close and reopen it to " +
            "see it here in the new language."),

        new Topic(
            "Visual Interface & On-Screen Keyboard",
            "Ultra Composer includes a modern, high-contrast dark theme (WCAG compliant) with " +
            "distinct accent colors per section and a left-side navigation bar:\n\n" +
            "- Left navigation bar: quickly switch between views (Instrument Bank, Sequencer, " +
            "Arranger, Multi-track & Mixer, Auto-accompaniment, Sound and effects) using a mouse or " +
            "the keyboard.\n" +
            "- On-screen keyboard: docked at the bottom of the window is a 5-octave piano keyboard " +
            "(C2 to C7). Keys light up in real time whenever a note sounds from any source " +
            "(computer keyboard, mouse click, or connected MIDI hardware). Clicking or dragging " +
            "across keys with the mouse plays notes directly. You can show or hide it at any time " +
            "via \"View\" -> \"Show on-screen keyboard\". The on-screen keyboard is purely a " +
            "visual aid and is excluded from the Tab order so it does not interfere with JAWS " +
            "screen reader navigation.\n" +
            "- Play notes from computer keyboard everywhere: by default, \"Play notes from computer " +
            "keyboard everywhere\" is turned on (under the \"View\" menu and as a checkbox in the " +
            "performance keyboard region). This lets sighted and keyboard users play notes using " +
            "keys Z-M and Q-U from anywhere in the window without needing to Tab into the " +
            "performance area first. Note input is automatically bypassed whenever focus is inside a " +
            "text entry box (such as track names or pitch inputs) and whenever Ctrl/Alt modifiers " +
            "are held for shortcuts and menus. JAWS users who prefer local-only note input can easily " +
            "uncheck this option at any time."),
    };
}
