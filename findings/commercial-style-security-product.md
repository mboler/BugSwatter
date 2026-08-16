# BugSwatter reviewing a commercial-style security product

These cases came from a commercial-style security product. Names, page text, paths, artwork, and business details have been replaced with generic equivalents. The defects and fixes retain their original technical shape.

## White diagram text failed contrast near one gradient edge

**Reported finding:** A diagram used white labels across a bright amber gradient. The darker edge passed contrast checks, but the brighter edge did not.

```svg
<linearGradient id="warningBand">
  <stop offset="0" stop-color="#a84d08" />
  <stop offset="1" stop-color="#f2a10b" />
</linearGradient>
<text fill="#ffffff">Constrained mode</text>
```

**Supporting context:** The text style and gradient definition were separated within a large SVG asset. Reviewing only the label edit or only the shared typography would not establish contrast across the complete fill range.

**Validator disposition:** Confirmed. Both gradient stops were darkened and the rendered asset was rechecked for WCAG AA contrast.

## A reduced-motion rule accelerated an infinite animation

**Reported finding:** A blanket reduced-motion rule set every animation duration to a tiny value. An infinite blinking animation therefore cycled every frame instead of stopping, which produced flicker for the users the rule was meant to protect.

```css
@media (prefers-reduced-motion: reduce) {
  *, *::before, *::after {
    animation-duration: 0.01ms !important;
  }
}
```

**Supporting context:** The blanket accessibility rule was far from the component's unchanged `animation-iteration-count: infinite` declaration. The problem appeared only when both rules were reviewed together.

**Validator disposition:** Confirmed. The infinite animation now receives an explicit `animation: none` override before the general reduced-motion rule.

## A child page marked its parent navigation link as the current page

**Reported finding:** Several child pages highlighted a parent navigation entry and used `aria-current="page"`. Visually the shared section was selected, but assistive technology was told that the parent URL was the current page.

```html
<a href="/section" class="is-active" aria-current="page">Section</a>
```

**Supporting context:** The shared navigation pattern intentionally highlighted the parent section, while the child document URL established that it was not the current page. Either file alone made the markup look plausible.

**Validator disposition:** Confirmed. The pages retain the visual parent highlight and use `aria-current="true"` to describe a current item without falsely identifying the current page URL.
