import { Component, ElementRef, ViewChild, signal } from '@angular/core';
import { CommonModule } from '@angular/common';

// A row-actions overflow menu — collapses several secondary actions behind
// one trigger instead of a row of always-visible icon buttons.
//
// The panel renders with `position: fixed`, anchored to the trigger's own
// on-screen coordinates at open time — NOT `position: absolute` relative to
// this component's own element. An in-table dropdown positioned relative to
// an ancestor gets silently clipped by that ancestor's overflow/scroll
// container (exactly what happened here: the credentials table needed
// `overflow-x-auto` so wide rows scroll instead of clipping, but in most
// browsers setting overflow-x also forces overflow-y to a computed
// scrolling value, which then clipped an absolutely-positioned dropdown
// panel too). `position: fixed` is immune to that, the same reason
// ModalComponent renders via fixed positioning instead of relative/absolute.
@Component({
  selector: 'app-dropdown-menu',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './dropdown-menu.component.html',
})
export class DropdownMenuComponent {
  @ViewChild('trigger') triggerRef!: ElementRef<HTMLElement>;

  readonly open = signal(false);
  readonly menuTop = signal(0);
  readonly menuRight = signal(0);

  toggle(): void {
    if (this.open()) {
      this.close();
      return;
    }
    const rect = this.triggerRef.nativeElement.getBoundingClientRect();
    this.menuTop.set(rect.bottom + 4);
    this.menuRight.set(window.innerWidth - rect.right);
    this.open.set(true);
  }

  close(): void {
    this.open.set(false);
  }
}
