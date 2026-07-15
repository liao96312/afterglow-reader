(() => {
  const content = document.getElementById('content');
  const chapters = document.getElementById('chapters');
  const openFile = document.getElementById('open-file');
  const directoryToggle = document.getElementById('directory-toggle');
  const autoScrollButton = document.getElementById('auto-scroll');
  const settingsToggle = document.getElementById('settings-toggle');
  const settingsPanel = document.getElementById('settings-panel');
  const fontFamily = document.getElementById('font-family');
  const fontSize = document.getElementById('font-size');
  const lineHeight = document.getElementById('line-height');
  const opacity = document.getElementById('opacity');
  const scrollSpeed = document.getElementById('scroll-speed');
  const appLabel = document.getElementById('app-label');
  const toast = document.getElementById('toast');
  let raf = 0;
  let targetScroll = 0;
  let autoScroll = false;
  let autoSpeed = 40;
  let lastFrame = 0;
  let windowRequestPending = false;
  let progressTimer = 0;
  let progressSequence = 0;
  let dragState = null;
  let toastTimer = 0;
  let toolbarHideTimer = 0;
  let readerInteractionRequested = false;

  const showToast = (message) => {
    if (!toast) return;
    window.clearTimeout(toastTimer);
    toast.textContent = message;
    toast.hidden = false;
    toastTimer = window.setTimeout(() => { toast.hidden = true; }, 3000);
  };

  const keepToolbarVisible = () => {
    document.body.classList.remove('toolbar-hidden');
    window.clearTimeout(toolbarHideTimer);
    toolbarHideTimer = window.setTimeout(() => {
      if (!chapters.classList.contains('hidden')) return;
      document.body.classList.add('toolbar-hidden');
    }, 4000);
  };

  openFile.onclick = () => {
    keepToolbarVisible();
    window.chrome?.webview?.postMessage(JSON.stringify({ type: 'openFile' }));
  };
  directoryToggle.onclick = () => {
    chapters.classList.toggle('hidden');
    keepToolbarVisible();
  };
  const publishSettings = () => {
    window.chrome?.webview?.postMessage(JSON.stringify({ type: 'settingsChanged', fontFamily: fontFamily.value, fontSize: Number(fontSize.value), lineHeight: Number(lineHeight.value), opacity: Number(opacity.value), scrollPixelsPerSecond: Number(scrollSpeed.value) }));
  };
  settingsToggle.onclick = () => { settingsPanel.classList.toggle('hidden'); keepToolbarVisible(); };
  [fontFamily, fontSize, lineHeight, opacity, scrollSpeed].forEach(control => control.addEventListener('input', publishSettings));
  window.applyReaderSettings = (settings) => {
    document.documentElement.style.setProperty('--font-family', settings.fontFamily || 'Microsoft YaHei');
    document.documentElement.style.setProperty('--font-size', `${settings.fontSize || 20}px`);
    document.documentElement.style.setProperty('--line-height', settings.lineHeight || 1.9);
    fontFamily.value = settings.fontFamily || 'Microsoft YaHei'; fontSize.value = settings.fontSize || 20; lineHeight.value = settings.lineHeight || 1.9; opacity.value = settings.opacity || 0.94; scrollSpeed.value = settings.scrollPixelsPerSecond || 220;
    autoSpeed = settings.scrollPixelsPerSecond || 220;
  };
  autoScrollButton.onclick = () => {
    window.toggleAutoScroll?.();
    keepToolbarVisible();
  };

  const postWindowAction = (message) => (event) => {
    if (event.button !== 0) return;
    event.preventDefault();
    event.stopPropagation();
    window.chrome?.webview?.postMessage(JSON.stringify(message));
  };

  appLabel.addEventListener('pointerdown', postWindowAction({ type: 'beginWindowDrag' }));
  for (const handle of document.querySelectorAll('.window-resize-handle')) {
    handle.addEventListener('pointerdown', postWindowAction({
      type: 'beginWindowResize',
      edge: handle.dataset.edge
    }));
  }

  const requestReaderInteraction = () => {
    if (readerInteractionRequested) return;
    readerInteractionRequested = true;
    window.chrome?.webview?.postMessage(JSON.stringify({ type: 'readerPointerEntered' }));
  };

  document.addEventListener('pointerenter', requestReaderInteraction, true);
  addEventListener('pointermove', requestReaderInteraction, { passive: true });
  window.resetReaderInteraction = () => { readerInteractionRequested = false; };

  window.setChapterIndex = (items) => {
    chapters.replaceChildren();
    for (const item of items) {
      const link = document.createElement('button');
      link.type = 'button';
      link.dataset.chapterId = item.id;
      link.textContent = item.title;
      link.onclick = () => window.chrome?.webview?.postMessage(JSON.stringify({ type: 'selectChapter', chapterId: item.id }));
      chapters.append(link);
    }
  };

  const setActiveChapter = (id) => {
    for (const link of chapters.querySelectorAll('button')) {
      link.classList.toggle('active', link.dataset.chapterId === id);
    }
  };

  const restoreAnchor = (anchorId, anchorOffset) => {
    if (!anchorId) return false;
    const anchor = document.querySelector(`[data-paragraph-id="${CSS.escape(anchorId)}"]`);
    if (!anchor) return false;
    const targetOffset = Number.isFinite(anchorOffset) ? anchorOffset : 0;
    anchor.scrollIntoView({ block: 'start' });
    const correct = () => {
      if (!anchor.isConnected) return;
      const delta = anchor.getBoundingClientRect().top - targetOffset;
      if (Math.abs(delta) > 0.5) scrollBy(0, delta);
      targetScroll = scrollY;
    };
    correct();
    requestAnimationFrame(correct);
    requestAnimationFrame(() => requestAnimationFrame(correct));
    return true;
  };

  window.renderWindow = (payload) => {
    const book = payload.book;
    content.replaceChildren();
    const title = document.createElement('h1');
    title.textContent = book.Title;
    content.append(title);
    for (const chapter of book.Chapters) {
      const section = document.createElement('section');
      section.id = chapter.Id;
      const heading = document.createElement('h2');
      heading.textContent = chapter.Title;
      section.append(heading);
      for (const paragraph of chapter.Paragraphs) {
        const node = document.createElement('p');
        node.dataset.paragraphId = paragraph.Id;
        node.innerHTML = paragraph.Html;
        section.append(node);
      }
      content.append(section);
    }
    showToast(`${book.Title} · ${book.Chapters.length} 章`);
    setActiveChapter(payload.selectedChapterId || book.Chapters[0]?.Id);
    if (!restoreAnchor(payload.anchorId, payload.anchorOffset)) {
      scrollTo(0, 0);
      targetScroll = scrollY;
    }
    windowRequestPending = false;
  };

  window.windowRequestHandled = () => { windowRequestPending = false; };

  const anchorForWindowRequest = () => {
    const nodes = [...document.querySelectorAll('[data-paragraph-id]')];
    const visible = nodes.find(node => node.getBoundingClientRect().bottom > 0);
    const anchor = visible || nodes[0];
    return anchor ? { id: anchor.dataset.paragraphId, offset: anchor.getBoundingClientRect().top } : { id: null, offset: 0 };
  };

  window.captureCurrentAnchor = () => ({ ...anchorForWindowRequest(), sequence: progressSequence });

  const requestWindow = (direction) => {
    if (windowRequestPending || !window.chrome?.webview) return;
    windowRequestPending = true;
    const anchor = anchorForWindowRequest();
    window.chrome.webview.postMessage(JSON.stringify({ type: 'requestWindow', direction, anchorId: anchor.id, anchorOffset: anchor.offset }));
  };

  const publishProgress = () => {
    progressTimer = 0;
    if (!window.chrome?.webview) return;
    const anchor = anchorForWindowRequest();
    window.chrome.webview.postMessage(JSON.stringify({ type: 'progressChanged', paragraphId: anchor.id, offset: anchor.offset, sequence: ++progressSequence }));
  };

  const updateActiveFromScroll = () => {
    const section = [...content.querySelectorAll('section')].find(node => node.getBoundingClientRect().bottom > 0);
    if (section) setActiveChapter(section.id);
  };

  const clampTarget = (value) => Math.max(0, Math.min(value, document.documentElement.scrollHeight - innerHeight));
  const animate = (now) => {
    const dt = Math.min(0.05, Math.max(0.001, (now - lastFrame) / 1000));
    lastFrame = now;
    if (autoScroll) targetScroll = clampTarget(targetScroll + autoSpeed * dt);
    const distance = targetScroll - scrollY;
    const maxStep = (autoScroll ? autoSpeed : 1600) * dt;
    const step = Math.sign(distance) * Math.min(Math.abs(distance) * Math.min(1, dt * 12), maxStep);
    if (Math.abs(step) > 0.1) scrollBy(0, step);
    if (autoScroll && scrollY >= document.documentElement.scrollHeight - innerHeight - 1) {
      autoScroll = false;
      targetScroll = scrollY;
    }
    if (autoScroll || Math.abs(targetScroll - scrollY) > 0.5) raf = requestAnimationFrame(animate);
    else raf = 0;
  };

  const startAnimation = () => {
    if (!raf) {
      lastFrame = performance.now();
      raf = requestAnimationFrame(animate);
    }
  };

  window.toggleAutoScroll = () => {
    autoScroll = !autoScroll;
    targetScroll = scrollY;
    autoScrollButton.classList.toggle('active', autoScroll);
    autoScrollButton.textContent = autoScroll ? '暂停滚动' : '自动滚动';
    if (autoScroll) startAnimation();
  };

  addEventListener('wheel', (event) => {
    if (event.ctrlKey) return;
    const unit = event.deltaMode === 1 ? 16 : event.deltaMode === 2 ? innerHeight : 1;
    const delta = event.deltaY * unit;
    if (event.deltaMode === 0 && Math.abs(delta) < 40) return;
    event.preventDefault();
    targetScroll = clampTarget(targetScroll + delta);
    startAnimation();
  }, { passive: false });

  const isInteractiveTarget = (target) => target instanceof Element
    && Boolean(target.closest('a, button, input, textarea, select, [contenteditable="true"], [data-window-action]'));

  addEventListener('pointerdown', (event) => {
    if (event.button !== 0 || isInteractiveTarget(event.target)) return;
    dragState = {
      pointerId: event.pointerId,
      startX: event.clientX,
      startY: event.clientY,
      startScroll: scrollY,
      active: false
    };
  });

  addEventListener('pointermove', (event) => {
    if (!dragState || dragState.pointerId !== event.pointerId) return;
    const dx = event.clientX - dragState.startX;
    const dy = event.clientY - dragState.startY;
    if (!dragState.active && Math.hypot(dx, dy) < 6) return;
    dragState.active = true;
    autoScroll = false;
    document.documentElement.classList.add('dragging');
    targetScroll = clampTarget(dragState.startScroll - dy);
    startAnimation();
    event.preventDefault();
  }, { passive: false });

  const finishDrag = (event) => {
    if (!dragState || dragState.pointerId !== event.pointerId) return;
    if (dragState.active) event.preventDefault();
    document.documentElement.classList.remove('dragging');
    dragState = null;
  };

  addEventListener('pointerup', finishDrag);
  addEventListener('pointercancel', finishDrag);
  addEventListener('pointermove', keepToolbarVisible, { passive: true });

  addEventListener('keydown', (event) => {
    if (!autoScroll || (event.target && ['INPUT', 'TEXTAREA', 'SELECT'].includes(event.target.tagName))) return;
    if (event.key === 'ArrowUp') { autoSpeed = Math.max(10, autoSpeed - 10); event.preventDefault(); }
    if (event.key === 'ArrowDown') { autoSpeed = Math.min(400, autoSpeed + 10); event.preventDefault(); }
    if (event.key === ' ') { autoScroll = false; targetScroll = scrollY; event.preventDefault(); }
  });

  addEventListener('scroll', () => {
    updateActiveFromScroll();
    if (!progressTimer) progressTimer = setTimeout(publishProgress, 250);
    if (innerHeight + scrollY > document.documentElement.scrollHeight - 500) requestWindow(1);
    if (scrollY < 500) requestWindow(-1);
  }, { passive: true });

  keepToolbarVisible();
  window.chrome?.webview?.postMessage(JSON.stringify({ type: 'readerReady' }));
})();
