trait Stream<'a, const N: usize>: Sized where Self: 'a {
    type Item<'b>: Copy where Self: 'b;
    const SIZE: usize;
    fn next(&'a mut self) -> Self::Item<'a>;
    fn take(mut self) {}
}
impl<'a, const N: usize> Stream<'a, N> for Buffer<'a, N> where Self: Sized {
    type Item<'b> = u8 where Self: 'b;
    const SIZE: usize = N;
    fn next(&'a mut self) -> Self::Item<'a> { 0 }
    fn take(mut self) {}
}
impl Buffer<'static, 4> { pub(crate) fn typed(self: Box<Self>) {} }
