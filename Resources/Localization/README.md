# Ultra Creative Suite - Lokalizacijski resursi

## Jezici dostupni

- **sr.json** - Srpski (Ekavica)
- **en.json** - Engleski
- **de.json** - Nemački
- **fr.json** - Francuski
- **es.json** - Španski
- **it.json** - Italijanski
- **ru.json** - Ruski
- **pl.json** - Poljski
- **pt.json** - Portugalski
- **zh.json** - Kineski (Simplified)

## Struktura JSON fajla

```json
{
  "metadata": {
    "version": "1.0",
    "language": "sr",
    "languageName": "Srpski (Ekavica)",
    "fallback": "en"
  },
  "translations": {
    "key_name": "Prevod na jeziku",
    "another_key": "Drugi prevod"
  }
}
```

## Kako koristiti

1. Korisnik preuzima `.json` fajl željenog jezika sa vašeg sajta
2. Postavlja ga u `/Resources/Localization/` folder
3. Aplikacija automatski učitava i koristi prevode
4. Ako ključ nedostaje, automatski se koristi engleski kao fallback

## Prilagođavanje

Za dodavanje novog prevoda:

1. Kopiraj jedan od postojećih `.json` fajlova
2. Zameni sve vrednosti sa prevodima na željenom jeziku
3. Sačuvaj sa odgovarajućim jezičkim kodom (npr. `nl.json` za Holandski)
4. Postavi ga u `/Resources/Localization/` folder

## Napomene

- Sve vrednosti su u UTF-8 formatu
- Fallback jezik je uvek Engleski
- Dinamički stringovi koriste `{0}`, `{1}` itd. za zamenu vrednosti
