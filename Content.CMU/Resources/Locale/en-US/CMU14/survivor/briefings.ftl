# Survivor spawn briefings.
#
# `cmu-survivor-intro-<faction>` is the introduction block shown when a survivor spawns; the matching
# `cmu-survivor-story-<faction>` is the situation block shown right after it. `{ $colony }` is substituted
# with the name of the map the round actually rolled, so one piece of text covers every map.
#
# These are the faction defaults, used on every map. A map that tells a different story declares a
# `survivorscenario` on its planet prototype, and a faction may then override either half through
# `scenario:` on its thirdParty prototype (see SurvivorBriefingRules). Anything a scenario leaves unset falls
# back to these defaults.
#
# Keep each message on ONE line: a Fluent continuation line may not start with '[' (that is variant syntax).

# --- PRODIGY PMC ---------------------------------------------------------------------------------------
cmu-survivor-intro-prodigy = [bold]You are a private military contractor employed by Prodigy Security.[/bold] Your team was hired to provide security for { $colony }, protecting company property, personnel, and infrastructure.
cmu-survivor-story-prodigy = The contract was supposed to be routine. Then communications began failing, personnel started disappearing, and reports of violence spread throughout the colony. Now screams and unfamiliar roars echo through the settlement, and whatever is responsible is clearly not human. Your team has already regrouped, but the situation is becoming increasingly difficult to control. Your contract may still be active, but surviving comes first. Secure what supplies you can and find a way out.

# --- WEYLAND-YUTANI PMC (SATURN) -----------------------------------------------------------------------
cmu-survivor-intro-wypmc = [bold]You are a member of Saturn, an elite private military unit contracted by Weyland-Yutani.[/bold] Your team was originally stationed at a classified research facility on another colony elsewhere on the same planet, providing security for sensitive corporate operations.
cmu-survivor-story-wypmc = Following a catastrophic outbreak at { $colony }, your unit was deployed from its original posting under emergency orders. Weyland-Yutani could not afford to let the outbreak spread beyond the affected colony and threaten its research facilities and ongoing operations elsewhere on the planet. Your mission was straightforward: enter the affected colony, eliminate the xenomorph infestation, and ensure that no biological specimens or sensitive research leave the area. But the operation has already gone badly. Your team has suffered casualties, the infestation is more widespread than anticipated, and communications with command have been lost. Whatever support was promised is no longer coming through. This is a corporate cleanup operation. Recover what is valuable, destroy what is not, and make sure nobody talks about what happened here.

# --- CLF SPEC OPS -------------------------------------------------------------------------------------
cmu-survivor-intro-clf = [bold]You are a member of the Colonial Liberation Front, an armed resistance movement fighting against corporate control.[/bold] Your cell came to { $colony } to organise local resistance and undermine the corporations' hold over the colony.
cmu-survivor-story-clf = The colony has descended into chaos. Screams echo through the settlements, communications have broken down, and people are disappearing. Whatever is responsible, it does not care who owns the colony or which side you fight for. Your cell has regrouped with its surviving members, but you have no idea how many of your comrades are still alive. Keep your people together, conserve your ammunition, and stay alert. Something else is hunting on this colony, and it is not interested in your cause.

# --- UNITED AMERICAS ARMY ------------------------------------------------------------------------------
cmu-survivor-intro-army = [bold]You are a member of the United Americas Army.[/bold] Your detachment was deployed to { $colony } to assist local authorities amid growing civil unrest, restore order, and protect the civilian population.
cmu-survivor-story-army = The civil unrest is now the least of your concerns. An outbreak of unknown hostile organisms has overwhelmed parts of the colony, leaving settlements in disarray and communications severely disrupted. Your squad has suffered losses, and communications with command have been lost. Your original orders are no longer practical, and you have no clear instructions on how to proceed. There is, however, still hope. The [bold]USS Bush[/bold] is confirmed to be in orbit and is preparing a rescue operation. You have no details on the responding force or when they will reach the surface, but evacuation may still be possible. Keep your squad organised, assist civilians where possible, and hold out until extraction arrives.

# --- USCM FORCE RECON ----------------------------------------------------------------------------------
cmu-survivor-intro-forecon = [bold]You are a member of the United States Colonial Marine Corps Force Reconnaissance.[/bold] Your team was deployed to { $colony } ahead of the main military presence to observe the growing civil unrest and report on local conditions.
cmu-survivor-story-forecon = Your reconnaissance mission has been disrupted by an outbreak of unknown hostile organisms. Your team has suffered casualties, communications with command have gone silent, and the situation has deteriorated far beyond the scope of your original assignment. There is, however, still hope. Your vessel, the [bold]USS Bush[/bold], is confirmed to be in orbit and is preparing a rescue operation. You have no details on the responding force or when they will reach the surface, but the USS Bush has not abandoned you. Keep your remaining team together, hold out until extraction arrives, and survive long enough to make it off the colony.

# --- FREE COMPANY RAIDERS ------------------------------------------------------------------------------
cmu-survivor-intro-freecompany = [bold]You are a member of the Free Company, a loose group of independent raiders and pirates.[/bold] Your crew came to { $colony } looking for valuable cargo, equipment, or anything else worth taking.
cmu-survivor-story-freecompany = It was supposed to be an easy score. The colony had plenty of resources, security was stretched thin, and nobody was likely to ask too many questions about missing property. Then the screams started. Something has torn through the settlement, and your crew is no longer the biggest threat around. Your transport and escape plans may already be compromised. Gather whatever you can, keep your crew together, and find a way off this rock.

# --- UNISC SQUAD ---------------------------------------------------------------------------------------
cmu-survivor-intro-unisc = [bold]You are a member of the United Nations Interstellar Stabilisation Command, a peacekeeping force deployed to { $colony } during a period of civil unrest.[/bold] Your mission was to support local authorities, protect civilians, and prevent the situation from escalating.
cmu-survivor-story-unisc = The outbreak has brought the civil unrest to an abrupt end. Unknown hostile organisms are attacking civilians and security personnel alike, and local authorities have lost control of the situation. Communications with command have become unreliable, and there is no confirmation of an evacuation or incoming reinforcements. Your mandate has not changed: protect civilian life. Keep your squad organised, assist survivors, and maintain what order you can. There are still people alive on this colony who need help.

# --- DUTCH'S DOZEN -------------------------------------------------------------------------------------
cmu-survivor-intro-dutch = [bold]You are a member of Major Alan "Dutch" Schaefer's elite team of experienced soldiers and specialists.[/bold] Your team was deployed to { $colony } to investigate a series of disappearances and reports of an unidentified hunter operating in the area.
cmu-survivor-story-dutch = The evidence points to something far beyond an ordinary hostile target: a Yautja, an extraterrestrial hunter equipped with advanced technology and weapons. Your team has been tracking it, and you are determined to bring it down. But now there is another problem. A xenomorph outbreak has swept through the colony, turning an already dangerous mission into a fight for survival against two deadly threats. Dutch has led this team through hell before. Regroup with your fellow survivors, put your skills to use, and stay alive. If it bleeds, you can kill it.
