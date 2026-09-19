fn identity<T>(value: T) -> T { value }
fn main() {
    println!("{}", identity::<i32>(42));
    println!("{}", identity(true));
}
