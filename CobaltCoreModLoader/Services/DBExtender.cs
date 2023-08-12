﻿using CobaltCoreModding.Definitions.ExternalItems;
using CobaltCoreModding.Definitions.ModContactPoints;
using CobaltCoreModLoader.Utils;
using HarmonyLib;
using Microsoft.Extensions.Logging;
using System.Collections;
using System.Data;
using System.Reflection;

namespace CobaltCoreModLoader.Services
{
    /// <summary>
    /// This serice can be used to run patches
    /// </summary>
    public class DBExtender : IDbRegistry
    {
        private static ILogger<DBExtender>? Logger;

        private Type card_type { get; init; }

        public DBExtender(CobaltCoreHandler cobaltCoreHandler, ModAssemblyHandler modAssemblyHandler, ILogger<DBExtender> logger)
        {
            Logger = logger;
            card_type = CobaltCoreHandler.CobaltCoreAssembly?.GetType("Card") ?? throw new Exception();
        }

        private Harmony? harmony;

        private void LoadDbManifests()
        {
            foreach (var manifest in ModAssemblyHandler.ModLookup.Select(e => e.Item3))
            {
                if (manifest == null) continue;
                manifest.LoadManifest(this);
            }
        }

        /// <summary>
        /// This functions hooks the extra data storage from DBExtender into the loading function of Cobalt Core DB.
        /// </summary>
        /// <exception cref="Exception"></exception>
        public void PatchDB()
        {
            //load

            harmony = new Harmony("modloader.dbextender");

            //patch DB

            var make_init_queue_function = db_type.GetMethod("MakeInitQueue") ?? throw new Exception("make init queue method not found");

            var make_init_queue_postfix = typeof(DBExtender).GetMethod("MakeInitQueue_Postfix", BindingFlags.Static | BindingFlags.NonPublic) ?? throw new Exception("make init queue postfix not found");

            harmony.Patch(make_init_queue_function, postfix: new HarmonyMethod(make_init_queue_postfix));

            //patch localisation loder

            var load_strings_for_locale_method = db_type.GetMethod("LoadStringsForLocale", BindingFlags.Public | BindingFlags.Static) ?? throw new Exception("load_strings_for_locale_method not found");

            var load_strings_for_locale_postfix = typeof(DBExtender).GetMethod("LoadStringsForLocale_PostFix", BindingFlags.Static | BindingFlags.NonPublic) ?? throw new Exception("make init queue postfix not found");

            harmony.Patch(load_strings_for_locale_method, postfix: new HarmonyMethod(load_strings_for_locale_postfix));

            LoadDbManifests();
        }

        Assembly ICobaltCoreContact.CobaltCoreAssembly => CobaltCoreHandler.CobaltCoreAssembly ?? throw new NullReferenceException();

        private static void LoadStringsForLocale_PostFix(string locale, ref Dictionary<string, string> __result)
        {
            //Find all localisations to be added.
            foreach (var card in registered_cards.Values.Concat(card_overwrites.Values))
            {
                if (card == null) continue;

                string? text = card.GetLocalisation(locale);
                if (text == null)
                    continue;
                //make card and get value
                var inst = Activator.CreateInstance(card.CardType);
                if (inst == null)
                {
                    Logger?.LogCritical($"Cannot spawn instance of card {card.CardType.Name} because no empty constructor found.");
                    continue;
                }

                var key = "card." + card.CardType.Name + ".name";
                if (!__result.TryAdd(key, text))
                    Logger?.LogCritical($"Cannot add {key} to localisations, since already know. skipping...");
            }
        }

        private static Type? __db_type = null;

        private static Type db_type
        {
            get
            {
                if (__db_type != null) return __db_type;

                return __db_type = (CobaltCoreHandler.CobaltCoreAssembly?.GetType("DB") ?? throw new Exception("DB not found."));
            }
        }

        private static Queue<Action> MakeInitQueue_Postfix(Queue<Action> __result)
        {
            //extract each action and splice in custom actions.

            var patched_result = new Queue<Action>();
            // Cobalt core loads logic types like cards, artifacts, midrowStuff, enemies, modifiers, backgrounds, maps
            patched_result.Enqueue(__result.Dequeue());
            // We patch our own items into the DB.
            patched_result.Enqueue(() => { InsertNewLogicItems(); });
            // Cobalt Core loads decks and status data.
            patched_result.Enqueue(__result.Dequeue());
            //we patch out own items into db.
            patched_result.Enqueue(() => { InsertNewDeckAndStatus(); });
            //cobalt core loads localisations.
            patched_result.Enqueue(__result.Dequeue());
            // We inject ourselves into the loader directly, so no extra here.
            // Cobalt Core loads platforms.
            patched_result.Enqueue(__result.Dequeue());
            // nothing to do for us here...
            // Cobalt Core loads story.
            patched_result.Enqueue(__result.Dequeue());
            // we apply any patches to story item.
            patched_result.Enqueue(() => { PatchStory(); });
            // cobalt core loads atlas
            patched_result.Enqueue(__result.Dequeue());
            //Sprite extender needs to remove stuff from atlas for overwrite...
            patched_result.Enqueue(() => { SpriteExtender.BreakAtlas(); });
            // cobalt core loads fonts
            patched_result.Enqueue(__result.Dequeue());
            // nothing to do here...
            // cobalt creates extra art dictionaries for various stuff.
            patched_result.Enqueue(__result.Dequeue());
            patched_result.Enqueue(() => { PatchExtraItemSprites(); });
            //cobalt core creates card, articfact meta, event choice functions and story commands.
            patched_result.Enqueue(__result.Dequeue());
            //we do our patches on that.
            patched_result.Enqueue(() => { PatchMetasAndStoryFunctions(); });
            //cobalt core does stuff not concering us.
            while (__result.Count > 0)
                patched_result.Enqueue(__result.Dequeue());
            //return new action queue
            return patched_result;
        }

        /// <summary>
        /// while card and artifact metas are generated by attribute, some last minute resolutions are needed.
        /// this includes card meta.deck for external decks, those ids are not know during compile time and thus cannot easily added into the attribute.
        /// </summary>
        private static void PatchMetasAndStoryFunctions()
        {
            IDictionary card_meta_dictionary = db_type.GetField("cardMetas")?.GetValue(null) as IDictionary ?? throw new Exception("card meta dictionary not found");

            {
                var deck_field = TypesAndEnums.CardMetaType.GetField("deck");
                if (deck_field != null)
                {
                    foreach (var card in registered_cards.Values.Where(e => e != null && e.ActualDeck != null))
                    {
                        if (card == null) continue;

                        if (!card_meta_dictionary.Contains(card.CardType.Name))
                            continue;
                        var meta = card_meta_dictionary[card.CardType.Name];
                        var deck_val = TypesAndEnums.IntToDeck(card.ActualDeck?.Id);
                        if (deck_val == null)
                        {
                            Logger?.LogError("External Card {0} bad actual deck definition", card.GlobalName);
                            continue;
                        }
                        deck_field.SetValue(meta, deck_val);
                    }

                    foreach (var overwrite in card_overwrites.Where(e => e.Value != null && e.Value.ActualDeck != null))
                    {
                        var card = overwrite.Value;
                        if (card == null) continue;

                        if (!card_meta_dictionary.Contains(overwrite.Value))
                            continue;
                        var meta = card_meta_dictionary[overwrite.Value];
                        var deck_val = TypesAndEnums.IntToDeck(card.ActualDeck?.Id);
                        if (deck_val == null)
                        {
                            Logger?.LogError("External Card {0} bad actual deck definition", card.GlobalName);
                            continue;
                        }
                        deck_field.SetValue(meta, deck_val);
                    }

                    var dont_offer_field = TypesAndEnums.CardMetaType.GetField("dontOffer") ?? throw new Exception("deck meta dont offer field not found");
                    var unreleased_field = TypesAndEnums.CardMetaType.GetField("unreleased") ?? throw new Exception("deck meta unreleased field not found");
                    var dont_loc_field = TypesAndEnums.CardMetaType.GetField("dontLoc") ?? throw new Exception("deck meta dontLoc field not found");
                    var upgrades_to_field = TypesAndEnums.CardMetaType.GetField("upgradesTo") ?? throw new Exception("deck meta upgradesTo field not found");
                    var rarity_field = TypesAndEnums.CardMetaType.GetField("rarity") ?? throw new Exception("deck meta rarity field not found");
                    var extra_glossary_field = TypesAndEnums.CardMetaType.GetField("extraGlossary") ?? throw new Exception("deck meta extraGlossary field not found");
                    var weird_card_field = TypesAndEnums.CardMetaType.GetField("weirdCard") ?? throw new Exception("deck meta weirdCard field not found");


                    foreach (var meta_overwrite in card_meta_overwrites)
                    {
                        if (!card_meta_dictionary.Contains(meta_overwrite.Key))
                        {
                            Logger?.LogInformation("ExternalCardMeta {0} no target for overwrite {1}.", meta_overwrite.Value.GlobalName, meta_overwrite.Key);
                            continue;
                        }
                        var original_meta = card_meta_dictionary[meta_overwrite.Key];

                        var new_meta = meta_overwrite.Value;
                        if (new_meta == null)
                            continue;
                        //only set fields that are not null
                        if (new_meta.Unreleased != null)
                        {
                            unreleased_field.SetValue(original_meta, new_meta.Unreleased);
                        }
                        if (new_meta.WeirdCard != null)
                        {
                            weird_card_field.SetValue(original_meta, new_meta.WeirdCard);
                        }
                        if (new_meta.ExtraGlossary != null)
                        {
                            extra_glossary_field.SetValue(original_meta, new_meta.ExtraGlossary);
                        }

                        if (new_meta.UpgradesTo != null)
                        {
                            var upgrade_vals = new_meta.UpgradesTo.Select(e => TypesAndEnums.IntToUpgrade(e)).Where(e => e != null).ToArray();
                            var upgrade_arr = Array.CreateInstance(TypesAndEnums.UpgradeType, upgrade_vals.Length);
                            for(int i = 0; i < upgrade_vals.Length; i++)
                            {
                                upgrade_arr.SetValue(upgrade_vals[i], i);
                            }
                            upgrades_to_field.SetValue(original_meta, upgrade_arr);
                        }

                        if (new_meta.Deck != null)
                        {
                            var deck_val = TypesAndEnums.IntToDeck(new_meta.Deck.Id);
                            deck_field.SetValue(original_meta, deck_val);
                        }

                        if (new_meta.DontLoc != null)
                        {
                            dont_loc_field.SetValue(original_meta, new_meta.DontLoc);
                        }

                        if (new_meta.DontOffer != null)
                        {
                            dont_offer_field.SetValue(original_meta, new_meta.DontOffer);
                        }
                        if (new_meta.Rarity != null)
                        {
                            var rarity_val = TypesAndEnums.IntToRarity(new_meta.Rarity);
                            if (rarity_val != null)
                                rarity_field.SetValue(original_meta, rarity_val);
                        }
                    }
                }
                else
                    throw new Exception("Deck field in card meta type not found.");
            }




        }





        /// <summary>
        /// Cards, decks, icons, maps, characters, artifacts and other things need their sprite additionaly registered in db.
        /// this is done here.
        /// </summary>
        private static void PatchExtraItemSprites()
        {
            //cards
            IDictionary card_art_dictionary = db_type.GetField("cardArt", BindingFlags.Static | BindingFlags.Public)?.GetValue(null) as IDictionary ?? throw new Exception("card art dictionary not found");

            foreach (var card in registered_cards.Values)
            {
                if (card == null) continue;
                var spr_val = TypesAndEnums.IntToSpr(card.CardArt.Id);
                if (spr_val == null)
                {
                    Logger?.LogError($"CardArt {card.GlobalName} wasn't resolved.");
                    continue;
                }
                if (card_art_dictionary.Contains(card.CardType.Name))
                {
                    Logger?.LogCritical($"Collision of type card {card.CardType.Name} detected. skipping");
                    continue;
                }
                else
                {
                    card_art_dictionary.Add(card.CardType.Name, spr_val);
                }
            }

            foreach (var overwrite in card_overwrites)
            {
                if (!card_art_dictionary.Contains(overwrite.Key))
                {
                    Logger?.LogWarning("Overwrite {0} failed because no such entry known in sprites.", overwrite.Key);
                    continue;
                }
                var spr_val = TypesAndEnums.IntToSpr(overwrite.Value.CardArt.Id);
                if (spr_val == null)
                {
                    Logger?.LogError("Unexpected null id in cardart sprite. For External card {0}", overwrite.Value.GlobalName);
                    continue;
                }
                card_art_dictionary[overwrite.Key] = spr_val;
            }

            //decks
            IDictionary deck_borders_dict = db_type.GetField("deckBorders")?.GetValue(null) as IDictionary ?? throw new Exception();
            IDictionary deck_borders_over_dict = db_type.GetField("deckBordersOver")?.GetValue(null) as IDictionary ?? throw new Exception();
            IDictionary card_art_deck_default_dict = db_type.GetField("cardArtDeckDefault")?.GetValue(null) as IDictionary ?? throw new Exception();
            foreach (var deck in registered_decks.Values)
            {
                if (deck.Id == null)
                {
                    Logger?.LogError("ExternalDeck {0} has no idea despite being registered", deck.GlobalName);
                    continue;
                }
                var deck_key = TypesAndEnums.IntToDeck(deck.Id);
                if (deck_key == null)
                {
                    Logger?.LogError("ExternalDeck {0} id couldn't be converted into deck enum", deck.GlobalName);
                    continue;
                }

                var border_spr = TypesAndEnums.IntToSpr(deck.BorderSprite.Id);
                if (border_spr == null)
                {
                    Logger?.LogError("ExternalDeck {0} border sprite id {1} couldn't be converted into spr enum", deck.GlobalName, deck.BorderSprite.Id?.ToString() ?? "NULL");
                    continue;
                }

                if (deck_borders_dict.Contains(deck_key))
                {
                    deck_borders_dict[deck_key] = border_spr;
                }
                else
                {
                    deck_borders_dict.Add(deck_key, border_spr);
                }


                var border_over_spr = TypesAndEnums.IntToSpr(deck.BordersOverSprite?.Id);
                if (deck.BordersOverSprite != null && border_over_spr == null)
                {
                    Logger?.LogError("ExternalDeck {0} border over sprite id {1} couldn't be converted into spr enum", deck.GlobalName, deck.BordersOverSprite.Id?.ToString() ?? "NULL");
                    continue;
                }

                if (border_over_spr != null)
                {
                    if (deck_borders_over_dict.Contains(deck_key))
                    {
                        deck_borders_over_dict[deck_key] = border_over_spr;
                    }
                    else
                    {
                        deck_borders_over_dict.Add(deck_key, border_over_spr);
                    }
                }

                var card_default_spr = TypesAndEnums.IntToSpr(deck.CardArtDefault.Id);
                if (card_default_spr == null)
                {
                    Logger?.LogError("ExternalDeck {0} card default sprite id {1} couldn't be converted into spr enum", deck.GlobalName, deck.CardArtDefault.Id?.ToString() ?? "NULL");
                    continue;
                }

                if (border_over_spr != null)
                {
                    if (card_art_deck_default_dict.Contains(deck_key))
                    {
                        card_art_deck_default_dict[deck_key] = card_default_spr;
                    }
                    else
                    {
                        card_art_deck_default_dict.Add(deck_key, card_default_spr);
                    }
                }

            }





        }

        /// <summary>
        /// Do anything to db.story.
        /// </summary>
        private static void PatchStory()
        {
        }

        /// <summary>
        /// Decks and Statuses need to be patched into DB.
        /// </summary>
        private static void InsertNewDeckAndStatus()
        {

            IDictionary deck_dict = db_type.GetField("decks")?.GetValue(null) as IDictionary ?? throw new Exception("decks dictinoary not found");

            var color_field = TypesAndEnums.DeckDefType.GetField("color") ?? throw new Exception("DeckDef.color not found");
            var title_color_field = TypesAndEnums.DeckDefType.GetField("titleColor") ?? throw new Exception("DeckDef.titleColor not found");

            foreach (var deck in registered_decks.Values)
            {
                var deck_val = TypesAndEnums.IntToDeck(deck.Id);
                if (deck_val == null)
                {
                    Logger?.LogError("externaldeck {0} id {1} not converted into deck enum. skipping...", deck.GlobalName, deck.Id?.ToString() ?? "NULL");
                    continue;
                }
                //create deck def object
                var new_deck_def = Activator.CreateInstance(TypesAndEnums.DeckDefType);

                var deck_color = Activator.CreateInstance(TypesAndEnums.CobaltColorType, (UInt32)deck.DeckColor.ToArgb());
                if (deck_color == null)
                {
                    Logger?.LogWarning("ExternalDeck {0} Color couldn't be initalized", deck.GlobalName);
                    continue;
                }
                var title_color = Activator.CreateInstance(TypesAndEnums.CobaltColorType, (UInt32)deck.TitleColor.ToArgb());
                if (title_color == null)
                {
                    Logger?.LogWarning("ExternalDeck {0} Title Color couldn't be initalized", deck.GlobalName);
                    continue;
                }

                color_field.SetValue(new_deck_def, deck_color);
                title_color_field.SetValue(new_deck_def, title_color);

                deck.DeckDefReference = new_deck_def;
                if (deck_dict.Contains(deck_val))
                {
                    deck_dict[deck_val] = new_deck_def;
                }
                else
                {
                    deck_dict.Add(deck_val, new_deck_def);
                }
            }

        }

        private static void InsertNewLogicItems()
        {
            if (CobaltCoreHandler.CobaltCoreAssembly != null)
            {
                /*
                //patch into all fields
                var db_type = CobaltCoreHandler.CobaltCoreAssembly.GetType("DB") ?? throw new Exception();

                var card_dict = db_type.GetField("cards")?.GetValue(null) as Dictionary<string, Type>;
                LoadAllSubclasses(card_dict, CobaltCoreHandler.CobaltCoreAssembly.GetType("Card"));

                var enemies_dict = db_type.GetField("enemies")?.GetValue(null) as Dictionary<string, Type>;
                LoadAllSubclasses(enemies_dict, CobaltCoreHandler.CobaltCoreAssembly.GetType("AI"));

                var modifiers_dict = db_type.GetField("modifiers")?.GetValue(null) as Dictionary<string, Type>;
                LoadAllSubclasses(modifiers_dict, CobaltCoreHandler.CobaltCoreAssembly.GetType("FightModifier"));

                var artifacts_dict = db_type.GetField("artifacts")?.GetValue(null) as Dictionary<string, Type>;
                LoadAllSubclasses(artifacts_dict, CobaltCoreHandler.CobaltCoreAssembly.GetType("Artifact"));

                var midrowStuff_dict = db_type.GetField("midrowStuff")?.GetValue(null) as Dictionary<string, Type>;
                LoadAllSubclasses(midrowStuff_dict, CobaltCoreHandler.CobaltCoreAssembly.GetType("StuffBase"));

                var backgrounds_dict = db_type.GetField("backgrounds")?.GetValue(null) as Dictionary<string, Type>;
                LoadAllSubclasses(backgrounds_dict, CobaltCoreHandler.CobaltCoreAssembly.GetType("BG"));

                var maps_dict = db_type.GetField("maps")?.GetValue(null) as Dictionary<string, Type>;
                LoadAllSubclasses(maps_dict, CobaltCoreHandler.CobaltCoreAssembly.GetType("MapBase"));
                */

                {
                    var card_dict = db_type.GetField("cards")?.GetValue(null) as Dictionary<string, Type>;

                    //Overwrite card dictionary.              

                    if (card_dict != null)
                    {

                        foreach (var card in registered_cards.Values)
                        {
                            if (card == null) continue;

                            if (!card_dict.TryAdd(card.CardType.Name, card.CardType))
                            {
                                Logger?.LogWarning($"ExternalCard {card.GlobalName} couldn't be added into DB.card dictionary because of collision in type name.");
                            }
                        }



                        foreach (var overwrite in card_overwrites)
                        {
                            if (!card_dict.ContainsKey(overwrite.Key))
                            {
                                Logger?.LogWarning("ExternalCard {0} overwrite of {1} failed because no key found.", overwrite.Value.GlobalName, overwrite.Key);
                                continue;
                            }
                            else
                            {
                                card_dict[overwrite.Key] = overwrite.Value.CardType;
                            }
                        }
                    }
                }



                var midrowStuff_dict = db_type.GetField("midrowStuff")?.GetValue(null) as Dictionary<string, Type>;
                var backgrounds_dict = db_type.GetField("backgrounds")?.GetValue(null) as Dictionary<string, Type>;
                var maps_dict = db_type.GetField("maps")?.GetValue(null) as Dictionary<string, Type>;
            }
        }

        /*
        private static void LoadAllSubclasses(Dictionary<string, Type>? target, Type? lookup_type)
        {
            if (target == null || lookup_type == null)
                return;

            if (ModStorage == null)
            {
                Logger?.LogCritical("LoadAllSubclasses was called without a mod assembly storage. No data provided!");
            }
            else
            {
                Logger?.LogInformation($"Loading '{lookup_type.Name}' subclasses");

                //locate all attributes.
                var qualifying_types = ModStorage.ModLookup.Select(e => e.Item1).SelectMany(asembly => asembly.GetTypes().Where(my_type => my_type.IsClass && !my_type.IsAbstract && my_type.IsSubclassOf(lookup_type)));
                //filter those with ignore attribute
                qualifying_types = qualifying_types.Where(type => Attribute.GetCustomAttribute(type, typeof(IgnoreModComponentAttribute)) == null);
                //sort into dictionary
                var counter = 0;
                foreach (var type in qualifying_types)
                {
                    if (target.TryAdd(type.Name, type))
                    {
                        Logger?.LogDebug("Loaded:" + type.Name);
                        counter++;
                    }
                    else
                        Logger?.LogWarning($"Type {type.Name} was already in dictionary. Naming conflict!");
                }
                Logger?.LogInformation($"Successfully loaded {counter} subclasses of {lookup_type.Name} from {ModStorage.ModLookup.Count()} mod assemblies.");
            }
        }
        */

        private static Dictionary<string, ExternalCard> registered_cards = new Dictionary<string, ExternalCard>();

        private static Dictionary<string, ExternalCard> card_overwrites = new Dictionary<string, ExternalCard>();

        bool IDbRegistry.RegisterArtifact(ExternalArtifact artifact)
        {
            throw new NotImplementedException();
        }



        bool IDbRegistry.RegisterCard(ExternalCard card, string? overwrite)
        {
            if (!card.CardType.IsSubclassOf(card_type))
            {
                Logger?.LogCritical("ExternalCard {0} isn't a card but type {1}", card.GlobalName, card.CardType.Name);
                return false;
            }

            if (registered_cards.ContainsKey(card.GlobalName))
            {
                Logger?.LogCritical($"Tried to register a card with the global name {card.GlobalName} twice! Rejecting second card");
                return false;
            }

            //check if card is valid

            if (!card.ValidReferences())
            {
                Logger?.LogCritical($"Card with gloabl name {card.GlobalName} has unregistered assets.");
                return false;
            }



            //mark overwrite
            if (overwrite != null)
            {
                if (card_overwrites.TryAdd(overwrite, card))
                {
                    Logger?.LogInformation("Collision in overwrite of type {0}. Replacing {1} with {2}", overwrite, card_overwrites[overwrite].GlobalName, card.GlobalName);
                    card_overwrites[overwrite] = card;
                }
            }
            else
            {
                //add card into registy
                registered_cards.Add(card.GlobalName, card);
            }

            return true;
        }



        bool IDbRegistry.RegisterCharacter(ExternalCharacter character)
        {
            throw new NotImplementedException();
        }

        private const int deck_counter_start = 10000;
        private static int deck_counter = deck_counter_start;

        private static Dictionary<string, ExternalDeck> deck_lookup = new Dictionary<string, ExternalDeck>();
        private static Dictionary<int, ExternalDeck> registered_decks = new Dictionary<int, ExternalDeck>();

        bool IDbRegistry.RegisterDeck(ExternalDeck deck, int? overwrite)
        {
            if (deck.Id != null)
            {
                Logger?.LogWarning("Deck already has an ID");
                return false;
            }
            if (string.IsNullOrWhiteSpace(deck.GlobalName))
            {
                Logger?.LogWarning("Deck with empty global name");
                return false;
            }
            if (deck_lookup.ContainsKey(deck.GlobalName))
            {
                Logger?.LogWarning("Deck GlobalName collision:" + deck.GlobalName);
                return false;
            }

            if (overwrite == null)
            {
                deck_lookup.Add(deck.GlobalName, deck);
                registered_decks.Add(deck_counter, deck);
                deck.Id = deck_counter;
                deck_counter++;
                return true;
            }
            else
            {
                if (overwrite < 0 || deck_counter_start <= overwrite)
                {
                    Logger?.LogError("Attempted overwrite of non card value");
                    return false;
                }

                deck_lookup.Add(deck.GlobalName, deck);
                if (!registered_decks.TryAdd(overwrite.Value, deck))
                {
                    Logger?.LogWarning("Collision Deck Overwrite between {0} and {1} on value {2}. {1} will be used unless other overwrite happens.",
                        registered_decks[overwrite.Value].GlobalName, deck.GlobalName, overwrite.Value, deck.GlobalName);
                    registered_decks[overwrite.Value] = deck;
                }
                deck.Id = overwrite.Value;
                return true;
            }
        }

        bool IDbRegistry.RegisterEnemy(ExternalEnemy enemy)
        {
            throw new NotImplementedException();
        }

        bool IDbRegistry.RegisterMidrowItem(ExternalMidrowItem midrowItem)
        {
            throw new NotImplementedException();
        }

        bool IDbRegistry.RegisterModifier(ExternalModifier modifier)
        {
            throw new NotImplementedException();
        }

        bool IDbRegistry.RegisterSpaceThing(ExternalSpaceThing spaceThing)
        {
            throw new NotImplementedException();
        }

        bool IDbRegistry.RegisterStatus(ExternalStatus status)
        {
            throw new NotImplementedException();
        }

        ExternalSprite? IDbRegistry.GetModSprite(string globalName)
        {
            return SpriteExtender.LookupSprite(globalName);
        }

        ExternalSprite IDbRegistry.GetOriginalSprite(int sprVal)
        {
            return TypesAndEnums.GetOriginalSprite(sprVal);
        }

        /// <summary>
        /// CardType -> Card Meta
        /// </summary>
        private static Dictionary<string, ExternalCardMeta> card_meta_overwrites = new Dictionary<string, ExternalCardMeta>();
        /// <summary>
        /// GlobalName -> Card Meta
        /// </summary>
        private static Dictionary<string, ExternalCardMeta> card_meta_lookup = new Dictionary<string, ExternalCardMeta>();

        bool IDbRegistry.RegisterCardMetaOverwrite(ExternalCardMeta cardMeta, string card_key)
        {
            if (string.IsNullOrEmpty(cardMeta.GlobalName))
            {
                Logger?.LogWarning("Attempted to register card meta without global name. rejected.");
                return false;
            }
            if (!card_meta_lookup.TryAdd(cardMeta.GlobalName, cardMeta))
            {
                Logger?.LogWarning("ExternalCardMeta {0} cannot be added, because global name already registered", cardMeta.GlobalName);
                return false;
            }
            if (!card_meta_overwrites.TryAdd(card_key, cardMeta))
            {
                Logger?.LogWarning("ExternalCardMeta {0} will overwrite anoter overwrite meta {1}", cardMeta.GlobalName, card_meta_lookup[card_key].GlobalName);
                card_meta_lookup[card_key] = cardMeta;
            }
            return true;
        }
    }
}