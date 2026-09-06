type Alias<T: Bound<Vec<i32>> + Copy> = Outer<Inner<T>>;
fn f<T: Copy, U>(value: T, other: U) -> T {
    let a: Outer<Inner<i32>>=value;
    let b: Vec<i32>=value;
    value
}
fn empty<>() {}
fn empty_bound<T:>() {}
fn trailing_bound<T: Copy+>() {}
