trait Mark {}
impl Mark for i32 {}
fn keep<T: Mark>(value: T) -> T { value }
fn main() { keep(1); }
