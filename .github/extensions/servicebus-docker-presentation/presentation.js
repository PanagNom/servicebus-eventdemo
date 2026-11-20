const slides = Array.from(document.querySelectorAll('.slide'));
const previous = document.getElementById('prevButton');
const next = document.getElementById('nextButton');
const progressText = document.getElementById('progressText');
const dots = document.getElementById('slideDots');
let current = 0;

slides.forEach((slide, index) => {
  const dot = document.createElement('button');
  dot.type = 'button';
  dot.className = 'dot-button';
  dot.setAttribute('aria-label', `Go to slide ${index + 1}: ${slide.dataset.title}`);
  dot.addEventListener('click', () => show(index));
  dots.appendChild(dot);
});

function show(index) {
  current = Math.max(0, Math.min(index, slides.length - 1));
  slides.forEach((slide, slideIndex) => {
    slide.classList.toggle('active', slideIndex === current);
  });
  Array.from(dots.children).forEach((dot, dotIndex) => {
    dot.classList.toggle('active', dotIndex === current);
  });
  progressText.textContent = `${current + 1} / ${slides.length}`;
  previous.disabled = current === 0;
  next.disabled = current === slides.length - 1;
  document.title = `${slides[current].dataset.title} — Docker Presentation`;
}

previous.addEventListener('click', () => show(current - 1));
next.addEventListener('click', () => show(current + 1));
document.addEventListener('keydown', event => {
  if (event.key === 'ArrowRight' || event.key === 'PageDown') show(current + 1);
  if (event.key === 'ArrowLeft' || event.key === 'PageUp') show(current - 1);
  if (event.key === 'Home') show(0);
  if (event.key === 'End') show(slides.length - 1);
});

show(0);
