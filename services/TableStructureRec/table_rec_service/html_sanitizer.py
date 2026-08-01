from __future__ import annotations

import html
from html.parser import HTMLParser


ALLOWED_ELEMENTS = {
    "html",
    "body",
    "table",
    "thead",
    "tbody",
    "tfoot",
    "colgroup",
    "col",
    "tr",
    "th",
    "td",
    "br",
}
SPAN_ATTRIBUTES = {"rowspan", "colspan"}
VOID_ELEMENTS = {"br", "col"}


class _TableSanitizer(HTMLParser):
    def __init__(self) -> None:
        super().__init__(convert_charrefs=True)
        self.output: list[str] = []
        self.open_elements: list[str] = []
        self.table_count = 0
        self.suppressed_depth = 0

    def handle_starttag(
        self, tag: str, attrs: list[tuple[str, str | None]]
    ) -> None:
        tag = tag.lower()
        if tag not in ALLOWED_ELEMENTS:
            self.suppressed_depth += 1
            return
        if self.suppressed_depth:
            return

        if tag == "table":
            self.table_count += 1
        rendered_attrs: list[str] = []
        for name, value in attrs:
            name = name.lower()
            if name not in SPAN_ATTRIBUTES or value is None or not value.isdigit():
                continue
            numeric = int(value)
            if 1 <= numeric <= 1_000:
                rendered_attrs.append(f' {name}="{numeric}"')
        self.output.append(f"<{tag}{''.join(rendered_attrs)}>")
        if tag not in VOID_ELEMENTS:
            self.open_elements.append(tag)

    def handle_startendtag(
        self, tag: str, attrs: list[tuple[str, str | None]]
    ) -> None:
        self.handle_starttag(tag, attrs)

    def handle_endtag(self, tag: str) -> None:
        tag = tag.lower()
        if tag not in ALLOWED_ELEMENTS:
            if self.suppressed_depth:
                self.suppressed_depth -= 1
            return
        if self.suppressed_depth or tag in VOID_ELEMENTS or tag not in self.open_elements:
            return

        while self.open_elements:
            current = self.open_elements.pop()
            self.output.append(f"</{current}>")
            if current == tag:
                break

    def handle_data(self, data: str) -> None:
        if not self.suppressed_depth:
            self.output.append(html.escape(data, quote=False))

    def finish(self) -> str:
        while self.open_elements:
            self.output.append(f"</{self.open_elements.pop()}>")
        return "".join(self.output) if self.table_count == 1 else ""


def sanitize_table_html(value: str) -> str:
    sanitizer = _TableSanitizer()
    sanitizer.feed(value)
    sanitizer.close()
    return sanitizer.finish()
